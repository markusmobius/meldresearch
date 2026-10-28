#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Raw | ConvertFrom-Json
if ($metadata.Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$' -or $metadata.ExtensionId -notmatch '^[a-p]{32}$') {
    throw 'Invalid Meld Research package metadata.'
}
$target = Join-Path $env:LOCALAPPDATA "MeldResearch\App\$($metadata.Version)"
if (Test-Path -LiteralPath $target) { throw 'This version is already installed. Use its Start-Meld.ps1 launcher; existing files were not replaced.' }
$null = New-Item -ItemType Directory -Path $target
foreach ($name in @('local', 'browser-host', 'frontend', 'browser', 'installation.json', 'Start-Meld.ps1', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target -Recurse
}
& (Join-Path $target 'browser-host\MeldResearch.BrowserHost.exe') --register
if ($LASTEXITCODE -ne 0) { throw 'Native-host registration failed. The browser extension has not been installed.' }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Meld Research.lnk'))
$shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = '-NoProfile -File "' + (Join-Path $target 'Start-Meld.ps1') + '"'
$shortcut.WorkingDirectory = $target
$shortcut.Save()
Write-Host 'Installed the per-user application and registered its native browser bridge.'
Write-Host 'The browser still requires your approval to add the extension. No browser policy was changed.'
& (Join-Path $target 'Start-Meld.ps1')
