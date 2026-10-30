#requires -Version 5.1
# Installs this release next to any earlier ones under %LOCALAPPDATA%\MeldResearch\App, points the stable
# launcher at it, registers the per-user native browser bridge, and starts Meld Research in the background at
# every sign-in (a per-user scheduled task; no administrator rights). Later releases install automatically.
# Running it again for an installed release repairs the launcher, shortcut and task without copying files.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Raw | ConvertFrom-Json
if ($metadata.Version -notmatch '^[0-9a-f]{64}$' -or $metadata.Runtime -ne 'win-x64' -or $metadata.ExtensionId -notmatch '^[a-p]{32}$') {
    throw 'Invalid Meld Research package metadata.'
}
$root = Join-Path $env:LOCALAPPDATA 'MeldResearch\App'
$folder = $metadata.Version.Substring(0, 12)
$target = Join-Path $root $folder
$launcher = Join-Path $root 'Start-Meld.ps1'
$port = 6001
$taskName = 'MeldResearch'
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$conhost = "$env:SystemRoot\System32\conhost.exe"
# Isolated tests set MELDRESEARCH_Install__Autostart=false: no scheduled task, and the launcher runs in this console.
$autostart = $env:MELDRESEARCH_Install__Autostart -ne 'false'

function Test-PortFree {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    try { $probe.Start(); $true } catch [Net.Sockets.SocketException] { $false } finally { $probe.Stop() }
}

Write-Host '  Stopping a running Meld Research, if any...'
if ($autostart -and (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}
# Only this installation's own launcher and application processes are stopped.
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'conhost.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($launcher, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process -Name 'MeldResearch.LocalBackend' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
for ($attempt = 0; $attempt -lt 40 -and -not (Test-PortFree); $attempt++) { Start-Sleep -Milliseconds 250 }
if (-not (Test-PortFree)) { throw "Port $port is used by another application. Close it and run the installer again; nothing was stopped." }

if (Test-Path -LiteralPath $target) {
    Write-Host "  Release $folder is already installed; reusing its files."
}
else {
    Write-Host "  Installing release $folder to $target"
    $partial = $target + '.partial'
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $partial -Force
    foreach ($name in @('local', 'browser-host', 'frontend', 'browser', 'installation.json', 'Start-Meld.ps1', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $partial -Recurse
    }
    Rename-Item -LiteralPath $partial -NewName $folder
}
# Isolated tests set MELDRESEARCH_Updates__RegisterNativeHost=false so the user's browser registration is untouched.
if ($env:MELDRESEARCH_Updates__RegisterNativeHost -ne 'false') {
    & (Join-Path $target 'browser-host\MeldResearch.BrowserHost.exe') --register
    if ($LASTEXITCODE -ne 0) { throw 'Native-host registration failed. The browser extension has not been installed.' }
}

$pointerPath = Join-Path $root 'current.json'
$previous = ''
if (Test-Path -LiteralPath $pointerPath) {
    $old = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
    $previous = if ([string]$old.current -eq $folder) { [string]$old.previous } else { [string]$old.current }
}
$json = ConvertTo-Json -InputObject ([ordered]@{ current = $folder; previous = $previous; rejected = @() })
[IO.File]::WriteAllText($pointerPath + '.partial', $json, (New-Object Text.UTF8Encoding $false))
Move-Item -LiteralPath ($pointerPath + '.partial') -Destination $pointerPath -Force
Copy-Item -LiteralPath (Join-Path $target 'Start-Meld.ps1') -Destination ($launcher + '.partial') -Force
Move-Item -LiteralPath ($launcher + '.partial') -Destination $launcher -Force

# The Start menu entry opens the running app, or starts it first. conhost --headless keeps the window hidden
# even when Windows Terminal is the default console.
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Meld Research.lnk'))
$shortcut.TargetPath = $conhost
$shortcut.Arguments = '--headless "' + $powershell + '" -NoProfile -ExecutionPolicy Bypass -File "' + $launcher + '" -Open'
$shortcut.WorkingDirectory = $root
$shortcut.IconLocation = "$env:SystemRoot\System32\shell32.dll,13"
$shortcut.Save()

if (-not $autostart) {
    Write-Host 'Installed the per-user application and pointed the Meld Research launcher at it.'
    & $launcher
    return
}

Write-Host '  Registering the sign-in task (runs as you, without administrator rights)...'
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $conhost -WorkingDirectory $root `
    -Argument ('--headless "' + $powershell + '" -NoProfile -ExecutionPolicy Bypass -File "' + $launcher + '" -Background')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -Priority 4
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$null = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'Starts the Meld Research local assistant at sign-in.' -Force
Start-ScheduledTask -TaskName $taskName

Write-Host '  Waiting for Meld Research to start...'
$running = $null
for ($attempt = 0; $attempt -lt 120 -and -not $running; $attempt++) {
    try {
        $status = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/version" -TimeoutSec 2
        if ($status.version -eq $metadata.Version) { $running = $status }
    }
    catch { }
    if (-not $running) { Start-Sleep -Milliseconds 500 }
}
if (-not $running) {
    $log = Join-Path $root 'launcher.log'
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 20 | ForEach-Object { Write-Host "    $_" } }
    throw "Meld Research did not start. The launcher log is $log."
}
if ($env:MELDRESEARCH_NO_BROWSER -ne 'true') {
    if ($metadata.StorePublished) { Start-Process ('https://chromewebstore.google.com/detail/' + $metadata.ExtensionId) }
    Start-Process "http://127.0.0.1:$port"
}
Write-Host ''
Write-Host '==========================================================================' -ForegroundColor Green
Write-Host "  [OK]  Meld Research is running at http://127.0.0.1:$port" -ForegroundColor Green
Write-Host '  [OK]  It starts in the background when you sign in and updates itself.' -ForegroundColor Green
Write-Host '==========================================================================' -ForegroundColor Green
Write-Host 'Link your Meld account under Account, approve the browser connector, then select projects and consent to processing.'
Write-Host 'The browser still requires your approval to add the extension. No browser policy was changed.'
