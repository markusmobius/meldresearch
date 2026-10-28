#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = [IO.Path]::Combine($PSScriptRoot, '..', 'artifacts', 'chrome-web-store')
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::Combine($PSScriptRoot, 'overleaf')
$store = [IO.Path]::Combine($PSScriptRoot, 'store')
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$manifest = Get-Content -LiteralPath ([IO.Path]::Combine($source, 'manifest.json')) -Raw | ConvertFrom-Json -AsHashtable

function Assert-Values($Actual, [string[]]$Expected, [string]$Label) {
    if (@(Compare-Object -ReferenceObject $Expected -DifferenceObject @($Actual)).Count) {
        throw "Unexpected $Label. Review the store disclosures before changing the package allowlist."
    }
}

function Assert-Png([string]$Path, [int]$Width, [int]$Height) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 33 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A' -or
        [Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ne 'IHDR') {
        throw "Invalid PNG asset: $Path"
    }
    $actualWidth = [int]$bytes[16] * 16777216 + [int]$bytes[17] * 65536 + [int]$bytes[18] * 256 + [int]$bytes[19]
    $actualHeight = [int]$bytes[20] * 16777216 + [int]$bytes[21] * 65536 + [int]$bytes[22] * 256 + [int]$bytes[23]
    if ($actualWidth -ne $Width -or $actualHeight -ne $Height) { throw "Incorrect PNG dimensions: $Path" }
}

Assert-Values @($manifest.Keys | Where-Object { $_ -ne 'key' }) @(
    'manifest_version', 'name', 'version', 'description', 'minimum_chrome_version',
    'incognito', 'icons', 'permissions', 'host_permissions', 'externally_connectable',
    'background', 'content_security_policy', 'action'
) 'manifest fields'
Assert-Values $manifest.icons.Keys @('16', '32', '48', '128') 'icon sizes'
Assert-Values $manifest.action.default_icon.Keys @('16', '32', '48') 'toolbar icon sizes'
if ($manifest.manifest_version -ne 3 -or $manifest.background.service_worker -ne 'background.js' -or
    $manifest.background.type -ne 'module' -or $manifest.action.default_popup -ne 'popup.html' -or
    $manifest.incognito -ne 'not_allowed') {
    throw 'Unexpected extension entry points or incognito behavior.'
}
if ([string]::IsNullOrWhiteSpace($manifest.name) -or $manifest.name.Length -gt 75 -or
    [string]::IsNullOrWhiteSpace($manifest.description) -or $manifest.description.Length -gt 132) {
    throw 'Chrome requires a name of at most 75 characters and a description of at most 132 characters.'
}
if ($manifest.version -notmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){0,3}$' -or
    @($manifest.version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count -or
    -not @($manifest.version.Split('.') | Where-Object { [int]$_ -gt 0 }).Count) {
    throw 'The version must contain one to four integers between 0 and 65535, with at least one nonzero component.'
}
Assert-Values $manifest.permissions @('nativeMessaging', 'scripting', 'storage', 'alarms') 'permissions'
Assert-Values $manifest.host_permissions @('https://www.overleaf.com/*') 'host permissions'
Assert-Values $manifest.externally_connectable.matches @('http://127.0.0.1/*', 'http://localhost/*') 'website messaging origins'
if ($manifest.externally_connectable.ContainsKey('ids') -or $manifest.ContainsKey('update_url') -or
    $manifest.ContainsKey('optional_permissions') -or $manifest.ContainsKey('optional_host_permissions') -or
    $manifest.content_security_policy.extension_pages -ne "script-src 'self'; object-src 'none'; base-uri 'none'") {
    throw 'Unexpected distribution, permission, messaging, or code-loading configuration.'
}

$files = [ordered]@{}
foreach ($name in @('background.js', 'content.js', 'reader.js', 'popup.html', 'popup.css', 'popup.js', 'privacy.html')) {
    $files[$name] = [IO.Path]::Combine($source, $name)
}
foreach ($size in @(16, 32, 48, 128)) {
    $entry = "icons/icon-$size.png"
    if ($manifest.icons["$size"] -ne $entry -or ($size -ne 128 -and $manifest.action.default_icon["$size"] -ne $entry)) {
        throw "The manifest must reference the $size-pixel icon."
    }
    $files[$entry] = [IO.Path]::Combine($source, 'icons', "icon-$size.png")
    Assert-Png $files[$entry] $size $size
}
$files['THIRD-PARTY-NOTICES.md'] = [IO.Path]::Combine($root, 'THIRD-PARTY-NOTICES.md')
foreach ($file in $files.Values) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing packaged file: $file" }
    if ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "A packaged file must not be a reparse point: $file"
    }
}
Assert-Png ([IO.Path]::Combine($store, 'small-promo-440x280.png')) 440 280

# Chrome supplies the store identity; the source key is only the current unpacked development identity.
$null = $manifest.Remove('key')
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes((($manifest | ConvertTo-Json -Depth 10) -replace "`r`n", "`n") + "`n")
$null = [IO.Directory]::CreateDirectory($output)
$filename = "meld-research-overleaf-$($manifest.version).zip"
$destination = [IO.Path]::Combine($output, $filename)
$temporary = [IO.Path]::Combine($output, ".$filename.$([Guid]::NewGuid().ToString('N')).tmp")
$timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('manifest.json') + @($files.Keys)) {
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $timestamp
            $entry.ExternalAttributes = 0
            $stream = $entry.Open()
            try {
                $bytes = if ($name -eq 'manifest.json') { $manifestBytes } else { [IO.File]::ReadAllBytes($files[$name]) }
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    [IO.File]::Move($temporary, $destination, $true)
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}

$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText([IO.Path]::Combine($output, "$filename.sha256"), "$hash  $filename`n", [Text.UTF8Encoding]::new($false))
$web = [IO.Path]::Combine($output, 'privacy-site')
$null = [IO.Directory]::CreateDirectory([IO.Path]::Combine($web, 'icons'))
foreach ($name in @('privacy.html', 'popup.css')) {
    [IO.File]::Copy([IO.Path]::Combine($source, $name), [IO.Path]::Combine($web, $name), $true)
}
[IO.File]::Copy($files['icons/icon-48.png'], [IO.Path]::Combine($web, 'icons', 'icon-48.png'), $true)
foreach ($name in @('listing.md', 'submission.md', 'small-promo-440x280.png')) {
    [IO.File]::Copy([IO.Path]::Combine($store, $name), [IO.Path]::Combine($output, $name), $true)
}
[IO.File]::Copy($files['icons/icon-128.png'], [IO.Path]::Combine($output, 'store-icon-128.png'), $true)
$screenshot = [IO.Path]::Combine($store, 'privacy-screenshot-1280x800.png')
if (Test-Path -LiteralPath $screenshot) {
    Assert-Png $screenshot 1280 800
    [IO.File]::Copy($screenshot, [IO.Path]::Combine($output, 'privacy-screenshot-1280x800.png'), $true)
}
else {
    Write-Warning 'No privacy-page screenshot is present. At least one genuine 1280x800 or 640x400 screenshot is required for submission.'
}

Write-Host "Draft-upload package: $destination"
Write-Host "SHA-256: $hash"
Write-Host "Submission material and public-policy files: $output"
Write-Host 'Nothing was uploaded, published, deployed, installed, or registered.'
Write-Warning 'Before submitting for review, resolve the identity, reviewer-access, live verification, and data-protection gates in submission.md.'
