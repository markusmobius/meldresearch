#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$StorePublished
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$frontend = [IO.Path]::Combine($root, 'apps', 'frontend')
$manifest = Get-Content -LiteralPath ([IO.Path]::Combine($root, 'browser', 'overleaf', 'manifest.json')) -Raw | ConvertFrom-Json
$hash = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($manifest.key))
$id = -join ($hash[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
$output = [IO.Path]::Combine($root, 'artifacts', 'desktop')
$staging = [IO.Path]::Combine($output, "$Runtime-$($manifest.version)-$([Guid]::NewGuid().ToString('N'))")
$null = [IO.Directory]::CreateDirectory($staging)
try {
    & npm --prefix $frontend run build
    if ($LASTEXITCODE -ne 0) { throw 'The frontend build failed.' }
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
    $installation = @{
        Version = $manifest.version
        ExtensionId = $id
        StoreUrl = "https://chromewebstore.google.com/detail/$id"
        StorePublished = [bool]$StorePublished
    }
    [IO.File]::WriteAllText([IO.Path]::Combine($staging, 'installation.json'),
        ($installation | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText([IO.Path]::Combine($staging, 'local', 'appsettings.json'),
        (@{ RemoteUrl = 'https://www.meldresearch.org'; ExtensionStorePublished = [bool]$StorePublished } | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
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
    Write-Host "Self-contained desktop TEST package: $archivePath"
    Write-Host 'This does not run the installer, publish a download, sign/notarize executables, or silently install a browser extension.'
}
finally {
    $resolved = Get-Item -LiteralPath $staging
    if ($resolved.Parent.FullName -ne $output) { throw 'The staging directory is outside the desktop artifacts directory.' }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
