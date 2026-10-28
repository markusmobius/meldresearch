#requires -Version 7.0
# Shared release helpers. Dot-source: . (Join-Path $PSScriptRoot 'release-common.ps1')

# Everything that ends up in, or determines, the desktop package. A change to any committed file here
# is a new client release; nothing else (for example the remote backend or deploy scripts) is.
$script:ClientReleaseInputs = @(
    'apps/LocalBackend', 'apps/BrowserHost', 'apps/frontend', 'libraries', 'browser/overleaf',
    'tools/package-local.ps1', 'tools/release-common.ps1', 'tools/install-windows.ps1', 'tools/start-windows.ps1',
    'tools/install-macos.command', 'tools/start-macos.command', 'THIRD-PARTY-NOTICES.md', 'Directory.Build.props', 'global.json'
)

function Get-ClientRelease {
    <# The client version is the SHA-256 of the committed client input tree (paths and blob hashes), so an
       unchanged client always has the same version. VersionDate orders releases and prevents downgrades. #>
    param([Parameter(Mandatory)][string]$Root, [switch]$AllowUncommitted)
    $pending = @(git -C $Root status --porcelain --untracked-files=all -- $script:ClientReleaseInputs)
    if ($LASTEXITCODE -ne 0) { throw 'Failed to check the desktop client inputs with git.' }
    if ($pending.Count -gt 0 -and -not $AllowUncommitted) {
        throw "Commit the desktop client changes first; an uncommitted build would reuse an older release version and be skipped by the updater:`n$($pending -join "`n")"
    }
    $tree = @(git -C $Root ls-tree -r HEAD -- $script:ClientReleaseInputs)
    if ($LASTEXITCODE -ne 0 -or $tree.Count -eq 0) { throw 'Failed to read the committed desktop client inputs.' }
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($tree -join "`n") + "`n"))
    $date = (git -C $Root log -1 --format=%cI HEAD -- $script:ClientReleaseInputs)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($date)) { throw 'Failed to read the desktop client commit date.' }
    [pscustomobject]@{
        Version = [Convert]::ToHexStringLower($hash)
        VersionDate = [DateTimeOffset]::Parse($date.Trim(), [Globalization.CultureInfo]::InvariantCulture)
        Uncommitted = $pending.Count -gt 0
    }
}

function Get-UpdatePublicKeyPath([string]$Root) { [IO.Path]::Combine($Root, 'apps', 'LocalBackend', 'update-signing-public-key.txt') }

function Get-UpdateSigningKeyPath {
    <# The private key is a passphrase-encrypted PKCS#8 PEM, readable only by the deploying user (mode 0600).
       On the server it lives in the deploy user's home, never where the web service account can read it. #>
    if ($env:MELDRESEARCH_UPDATE_SIGNING_KEY) { return [IO.Path]::GetFullPath($env:MELDRESEARCH_UPDATE_SIGNING_KEY) }
    $base = if ($IsWindows) { [Environment]::GetFolderPath('ApplicationData') }
        else { [IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), '.config') }
    [IO.Path]::Combine($base, 'meldresearch', 'update-signing-key.pem')
}

function Read-SigningPassphrase([string]$Prompt = 'Update signing key passphrase', [switch]$Confirm) {
    # Isolated tests only may supply the passphrase through the environment; releases prompt for it.
    if ($env:MELDRESEARCH_UPDATE_SIGNING_PASSPHRASE) { return $env:MELDRESEARCH_UPDATE_SIGNING_PASSPHRASE }
    $plain = [Net.NetworkCredential]::new('', (Read-Host -Prompt $Prompt -AsSecureString)).Password
    if ($Confirm) {
        if ($plain.Length -lt 12) { throw 'Use a passphrase of at least 12 characters.' }
        if ([Net.NetworkCredential]::new('', (Read-Host -Prompt 'Repeat the passphrase' -AsSecureString)).Password -ne $plain) {
            throw 'The passphrases do not match.'
        }
    }
    $plain
}

function Assert-TrustedSigningKey([Security.Cryptography.ECDsa]$Key, [string]$Root) {
    $trusted = (Get-Content -LiteralPath (Get-UpdatePublicKeyPath $Root) -Raw).Trim()
    if ([Convert]::ToBase64String($Key.ExportSubjectPublicKeyInfo()) -ne $trusted) {
        throw 'This update signing key does not match apps/LocalBackend/update-signing-public-key.txt; installed clients would reject its releases.'
    }
}

function Save-UpdateSigningKey([Security.Cryptography.ECDsa]$Key, [string]$Path, [string]$Passphrase) {
    $parameters = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
        [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
    $directory = Split-Path -Parent $Path
    $null = [IO.Directory]::CreateDirectory($directory)
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($directory, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute') }
    $temporary = "$Path.partial"
    $options = [IO.FileStreamOptions]@{ Mode = 'CreateNew'; Access = 'Write' }
    if (-not $IsWindows) { $options.UnixCreateMode = [IO.UnixFileMode]'UserRead, UserWrite' }
    $stream = [IO.FileStream]::new($temporary, $options)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer.Write($Key.ExportEncryptedPkcs8PrivateKeyPem($Passphrase, $parameters))
        $writer.Dispose()
    }
    finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $Path, $false)
}

function Open-UpdateSigningKey {
    <# Prompts for the passphrase and returns the private ECDSA key after confirming it is the one clients trust. #>
    param([Parameter(Mandatory)][string]$Root)
    $path = Get-UpdateSigningKeyPath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "No update signing key at $path. Install it with tools/update-signing-key.ps1 -Install <encrypted PEM>, or create one with -Create."
    }
    if (-not $IsWindows -and ([IO.File]::GetUnixFileMode($path) -band [IO.UnixFileMode]'GroupRead, GroupWrite, OtherRead, OtherWrite')) {
        throw "$path is readable by other users. Run: chmod 600 '$path'"
    }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromEncryptedPem((Get-Content -LiteralPath $path -Raw), (Read-SigningPassphrase))
        Assert-TrustedSigningKey $key $Root
    }
    catch [Security.Cryptography.CryptographicException] { $key.Dispose(); throw 'Wrong passphrase, or the signing key file is damaged.' }
    catch { $key.Dispose(); throw }
    $key
}

function New-SignedUpdate {
    <# Writes the signed manifest envelope for a built package. The signature covers the exact manifest bytes. #>
    param([Parameter(Mandatory)][Security.Cryptography.ECDsa]$Key, [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][DateTimeOffset]$VersionDate,
        [Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$Destination)
    $manifest = [ordered]@{
        product = 'MeldResearch.Desktop'; runtime = $Runtime; version = $Version
        versionDate = $VersionDate.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        length = (Get-Item -LiteralPath $PackagePath).Length
        sha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signature = $Key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $envelope = [ordered]@{ manifest = [Convert]::ToBase64String($bytes); signature = [Convert]::ToBase64String($signature) }
    [IO.File]::WriteAllText($Destination, ($envelope | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}

function Read-SignedUpdate {
    <# Decodes (without verifying) a signed manifest envelope, for comparing release versions. #>
    param([Parameter(Mandatory)][string]$Path)
    $envelope = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.manifest)) | ConvertFrom-Json
}
