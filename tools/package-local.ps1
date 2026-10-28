#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$StorePublished,
    # Release identity; computed from the committed client inputs when omitted (see release-common.ps1).
    [string]$ClientVersion,
    [Nullable[DateTimeOffset]]$ClientVersionDate,
    # Local test builds from uncommitted sources are allowed, but never update themselves.
    [switch]$AllowUncommitted,
    [string]$OutputDirectory,
    [string]$RemoteUrl = 'https://www.meldresearch.org',
    # Isolated tests only: trust a different update-signing public key file.
    [string]$UpdatePublicKeyPath,
    [switch]$SkipFrontendBuild
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
. (Join-Path $PSScriptRoot 'release-common.ps1')
$updatesEnabled = $true
if ([string]::IsNullOrEmpty($ClientVersion)) {
    $release = Get-ClientRelease -Root $root -AllowUncommitted:$AllowUncommitted
    $ClientVersion = $release.Version
    $ClientVersionDate = $release.VersionDate
    $updatesEnabled = -not $release.Uncommitted
}
if ($ClientVersion -notmatch '^[0-9a-f]{64}$' -or $null -eq $ClientVersionDate) { throw 'A 64-character lowercase hex ClientVersion and a ClientVersionDate are required.' }
if (-not $UpdatePublicKeyPath) { $UpdatePublicKeyPath = Get-UpdatePublicKeyPath $root }
if (-not (Test-Path -LiteralPath $UpdatePublicKeyPath)) { throw "Missing update signing public key $UpdatePublicKeyPath. Run tools\update-signing-key.ps1 -Create once." }
$updatePublicKey = (Get-Content -LiteralPath $UpdatePublicKeyPath -Raw).Trim()
$frontend = [IO.Path]::Combine($root, 'apps', 'frontend')
$manifest = Get-Content -LiteralPath ([IO.Path]::Combine($root, 'browser', 'overleaf', 'manifest.json')) -Raw | ConvertFrom-Json
$hash = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($manifest.key))
$id = -join ($hash[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::Combine($root, 'artifacts', 'desktop') }
$null = [IO.Directory]::CreateDirectory($output)
$staging = [IO.Path]::Combine($output, "$Runtime-$($ClientVersion.Substring(0, 12))-$([Guid]::NewGuid().ToString('N'))")
$null = [IO.Directory]::CreateDirectory($staging)
try {
    if (-not $SkipFrontendBuild) {
        & npm --prefix $frontend run build
        if ($LASTEXITCODE -ne 0) { throw 'The frontend build failed.' }
    }
    foreach ($app in @('LocalBackend', 'BrowserHost')) {
        $folder = if ($app -eq 'LocalBackend') { 'local' } else { 'browser-host' }
        & dotnet publish ([IO.Path]::Combine($root, 'apps', $app, "MeldResearch.$app.csproj")) -c Release -r $Runtime --self-contained true `
            -o ([IO.Path]::Combine($staging, $folder)) --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publishing $app failed." }
    }
    Copy-Item -LiteralPath ([IO.Path]::Combine($frontend, 'dist', 'frontend', 'browser')) `
        -Destination ([IO.Path]::Combine($staging, 'frontend')) -Recurse
    $null = [IO.Directory]::CreateDirectory([IO.Path]::Combine($staging, 'browser'))
    Copy-Item -LiteralPath ([IO.Path]::Combine($root, 'browser', 'overleaf')) `
        -Destination ([IO.Path]::Combine($staging, 'browser', 'overleaf')) -Recurse
    Copy-Item -LiteralPath ([IO.Path]::Combine($root, 'THIRD-PARTY-NOTICES.md')) -Destination $staging
    $installation = [ordered]@{
        Version = $ClientVersion
        VersionDate = ([DateTimeOffset]$ClientVersionDate).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        Runtime = $Runtime
        ConnectorVersion = $manifest.version
        ExtensionId = $id
        StoreUrl = "https://chromewebstore.google.com/detail/$id"
        StorePublished = [bool]$StorePublished
    }
    [IO.File]::WriteAllText([IO.Path]::Combine($staging, 'installation.json'),
        ($installation | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $settings = [ordered]@{
        RemoteUrl = $RemoteUrl; ExtensionStorePublished = [bool]$StorePublished
        ClientVersion = $ClientVersion; ClientVersionDate = $installation.VersionDate; ClientRuntime = $Runtime
        UpdatePublicKey = $updatePublicKey; UpdatesEnabled = $updatesEnabled
    }
    [IO.File]::WriteAllText([IO.Path]::Combine($staging, 'local', 'appsettings.json'),
        ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    if ($Runtime -eq 'win-x64') {
        Copy-Item -LiteralPath ([IO.Path]::Combine($PSScriptRoot, 'install-windows.ps1')) -Destination ([IO.Path]::Combine($staging, 'Install-Meld.ps1'))
        Copy-Item -LiteralPath ([IO.Path]::Combine($PSScriptRoot, 'start-windows.ps1')) -Destination ([IO.Path]::Combine($staging, 'Start-Meld.ps1'))
    }
    else {
        Copy-Item -LiteralPath ([IO.Path]::Combine($PSScriptRoot, 'install-macos.command')) -Destination ([IO.Path]::Combine($staging, 'Install-Meld.command'))
        Copy-Item -LiteralPath ([IO.Path]::Combine($PSScriptRoot, 'start-macos.command')) -Destination ([IO.Path]::Combine($staging, 'Start-Meld.command'))
    }
    $archivePath = [IO.Path]::Combine($output, "meldresearch-$Runtime.zip")
    $temporary = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
        if ($Runtime.StartsWith('osx-')) {
            $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Update)
            try {
                foreach ($entry in $archive.Entries) {
                    if ($entry.Name.EndsWith('.command') -or $entry.Name -in @('MeldResearch.LocalBackend', 'MeldResearch.BrowserHost') -or $entry.Name.EndsWith('.dylib')) {
                        $entry.ExternalAttributes = ([int]0x81ed -shl 16)
                    }
                }
            }
            finally { $archive.Dispose() }
        }
        [IO.File]::Move($temporary, $archivePath, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archivePath.sha256", "$checksum  $([IO.Path]::GetFileName($archivePath))`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Self-contained desktop TEST package: $archivePath (release $($ClientVersion.Substring(0, 12)), automatic updates: $updatesEnabled)"
    Write-Host 'This does not run the installer, publish a download, sign/notarize executables, or silently install a browser extension.'
}
finally {
    $resolved = Get-Item -LiteralPath $staging
    if ($resolved.Parent.FullName -ne $output) { throw 'The staging directory is outside the desktop artifacts directory.' }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
