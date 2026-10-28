#requires -Version 5.1
# Installs this release next to any earlier ones under %LOCALAPPDATA%\MeldResearch\App, points the stable
# launcher at it, and registers the per-user native browser bridge. Later releases install automatically.
$ErrorActionPreference = 'Stop'
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Raw | ConvertFrom-Json
if ($metadata.Version -notmatch '^[0-9a-f]{64}$' -or $metadata.Runtime -ne 'win-x64' -or $metadata.ExtensionId -notmatch '^[a-p]{32}$') {
    throw 'Invalid Meld Research package metadata.'
}
$root = Join-Path $env:LOCALAPPDATA 'MeldResearch\App'
$folder = $metadata.Version.Substring(0, 12)
$target = Join-Path $root $folder
if (Test-Path -LiteralPath $target) { throw 'This version is already installed. Start Meld Research from the Start menu; existing files were not replaced.' }
$null = New-Item -ItemType Directory -Path $target -Force
foreach ($name in @('local', 'browser-host', 'frontend', 'browser', 'installation.json', 'Start-Meld.ps1', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target -Recurse
}
# Isolated tests set MELDRESEARCH_Updates__RegisterNativeHost=false so the user's browser registration is untouched.
if ($env:MELDRESEARCH_Updates__RegisterNativeHost -ne 'false') {
    & (Join-Path $target 'browser-host\MeldResearch.BrowserHost.exe') --register
    if ($LASTEXITCODE -ne 0) { throw 'Native-host registration failed. The browser extension has not been installed.' }
}

$pointerPath = Join-Path $root 'current.json'
$previous = ''
if (Test-Path -LiteralPath $pointerPath) { $previous = [string](Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json).current }
$json = ConvertTo-Json -InputObject ([ordered]@{ current = $folder; previous = $previous; rejected = @() })
[IO.File]::WriteAllText($pointerPath + '.partial', $json, (New-Object Text.UTF8Encoding $false))
Move-Item -LiteralPath ($pointerPath + '.partial') -Destination $pointerPath -Force
$launcher = Join-Path $root 'Start-Meld.ps1'
Copy-Item -LiteralPath (Join-Path $target 'Start-Meld.ps1') -Destination ($launcher + '.partial') -Force
Move-Item -LiteralPath ($launcher + '.partial') -Destination $launcher -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Meld Research.lnk'))
$shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = '-NoProfile -File "' + $launcher + '"'
$shortcut.WorkingDirectory = $root
$shortcut.Save()
Write-Host 'Installed the per-user application and pointed the Meld Research launcher at it.'
Write-Host 'The browser still requires your approval to add the extension. No browser policy was changed.'
& $launcher
