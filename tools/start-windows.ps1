#requires -Version 5.1
# Stable Meld Research launcher. It lives in the application root next to current.json and starts the version
# that file names. When the application exits with code 75 it has installed a verified update and repointed
# current.json, so the launcher starts the new version. A version that fails to start is rolled back.
#   -Background  started by the sign-in task: no browser, output in launcher.log, quiet if already running.
#   -Open        Start menu: open the running app, or start it in the background first.
param([switch]$NoBrowser, [switch]$Background, [switch]$Open)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pointerPath = Join-Path $root 'current.json'
$updateExitCode = 75
$port = 6001
if (-not (Test-Path -LiteralPath $pointerPath)) { throw 'Meld Research is not installed here. Run Install-Meld.ps1 from the downloaded package.' }

function Test-Running {
    try { $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/status" -UseBasicParsing -TimeoutSec 2; $true } catch { $false }
}

if ($Open) {
    if (-not (Test-Running)) {
        if (Get-ScheduledTask -TaskName 'MeldResearch' -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName 'MeldResearch' }
        else {
            Start-Process -FilePath "$env:SystemRoot\System32\conhost.exe" -ArgumentList @('--headless',
                "`"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`"", '-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', "`"$PSCommandPath`"", '-Background')
        }
        for ($attempt = 0; $attempt -lt 120 -and -not (Test-Running); $attempt++) { Start-Sleep -Milliseconds 500 }
    }
    Start-Process "http://127.0.0.1:$port"
    return
}
if ($Background) {
    $NoBrowser = $true
    if (Test-Running) { return }
    $null = Start-Transcript -LiteralPath (Join-Path $root 'launcher.log') -Force
}

function Write-Pointer([string]$Current, [string]$Previous, [string[]]$Rejected) {
    $json = ConvertTo-Json -InputObject ([ordered]@{ current = $Current; previous = $Previous; rejected = @($Rejected) })
    [IO.File]::WriteAllText($pointerPath + '.partial', $json, (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath ($pointerPath + '.partial') -Destination $pointerPath -Force
}

$opened = $false
while ($true) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    try { $probe.Start() }
    catch [Net.Sockets.SocketException] { throw "Port $port is occupied. No process was stopped. Close the existing application before starting another." }
    finally { $probe.Stop() }

    $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
    if ($pointer.current -notmatch '^[0-9a-f]{12}$') { throw 'The installation pointer is invalid.' }
    $directory = Join-Path $root $pointer.current
    $metadata = Get-Content -LiteralPath (Join-Path $directory 'installation.json') -Raw | ConvertFrom-Json
    # In the background there is no console to read, so the application's log goes to files; the previous run's log is
    # kept so a restart into an update does not erase what happened before it. The logs omit research content.
    $output = @{}
    if ($Background) {
        foreach ($name in 'backend', 'backend-errors') {
            $log = Join-Path $root "$name.log"
            if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination (Join-Path $root "$name.previous.log") -Force }
        }
        $output = @{ RedirectStandardOutput = (Join-Path $root 'backend.log'); RedirectStandardError = (Join-Path $root 'backend-errors.log') }
    }
    $process = Start-Process -FilePath (Join-Path $directory 'local\MeldResearch.LocalBackend.exe') `
        -WorkingDirectory (Join-Path $directory 'local') `
        -ArgumentList @('--Port', $port, '--FrontendPath', ('"' + (Join-Path $directory 'frontend') + '"'),
            '--ConnectorDirectory', ('"' + (Join-Path $directory 'browser\overleaf') + '"'), '--InstallRoot', ('"' + $root + '"')) `
        -PassThru -NoNewWindow @output
    $null = $process.Handle # Required for ExitCode to be available after the process ends.
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            if ($process.HasExited) { break }
            try { $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/status" -UseBasicParsing -TimeoutSec 2; $ready = $true; break }
            catch { Start-Sleep -Milliseconds 250 }
        }
        if (-not $ready) {
            $previous = [string]$pointer.previous
            if ($previous -match '^[0-9a-f]{12}$' -and (Test-Path -LiteralPath (Join-Path $root $previous))) {
                Write-Warning "Meld Research $($pointer.current) did not start. Returning to the previous version $previous."
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
                if ($env:MELDRESEARCH_Updates__RegisterNativeHost -ne 'false') {
                    & (Join-Path $root "$previous\browser-host\MeldResearch.BrowserHost.exe") --register
                }
                Write-Pointer $previous '' (@($pointer.rejected) + $metadata.Version | Where-Object { $_ })
                Copy-Item -LiteralPath (Join-Path $root "$previous\Start-Meld.ps1") -Destination (Join-Path $root 'Start-Meld.ps1.partial') -Force
                Move-Item -LiteralPath (Join-Path $root 'Start-Meld.ps1.partial') -Destination (Join-Path $root 'Start-Meld.ps1') -Force
                continue
            }
            throw 'Meld did not become ready. Review the message in this terminal.'
        }
        if (-not $opened) {
            $opened = $true
            if (-not $NoBrowser -and $env:MELDRESEARCH_NO_BROWSER -ne 'true') {
                if ($metadata.StorePublished) {
                    if ($metadata.ExtensionId -notmatch '^[a-p]{32}$') { throw 'Invalid connector identity.' }
                    Start-Process ('https://chromewebstore.google.com/detail/' + $metadata.ExtensionId)
                }
                Start-Process "http://127.0.0.1:$port"
            }
            Write-Host 'Link your existing Google-based Meld account, install/approve the connector, then select projects and consent to Foundry processing.'
            if (-not $Background) { Write-Host 'Keep this terminal open. Ctrl+C stops this owned application instance. Updates install automatically.' }
        }
        $process.WaitForExit()
        if ($process.ExitCode -eq $updateExitCode) {
            Write-Host 'Restarting Meld Research into the installed update...'
            continue
        }
        break
    }
    finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }; $process.Dispose() }
}
