#requires -Version 7.0
<#
.SYNOPSIS
Real Windows one-line install -> automatic signed update -> launcher restart -> repair check for the desktop package.

.DESCRIPTION
Builds two real win-x64 packages with synthetic release versions, publishes the older one through a local remote
backend, and installs it with the account page's "irm ... | iex" command under Windows PowerShell 5.1. Then it
publishes the newer one and waits for the installed application to update and restart itself, publishes an older
signed release and confirms it is ignored, and re-runs the command to confirm a repair keeps the rollback target.
Everything is isolated: temporary LOCALAPPDATA/APPDATA/data directories, a throwaway signing key, no browser
windows, no scheduled task and no native-host (registry) registration. Takes several minutes; makes no model calls.
#>
param([switch]$KeepTemporary)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. ([IO.Path]::Combine($root, 'tools', 'release-common.ps1'))
$temp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'meldresearch-update-e2e-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temp)
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$localPort = 6001

function Start-Owned([string]$File, [string[]]$Arguments, [hashtable]$Environment, [string]$Log) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in $Environment.Keys) { $info.Environment[$name] = $Environment[$name] }
    # Like a normal console: Windows PowerShell must not inherit this PowerShell 7 session's module path.
    if ($File -like '*WindowsPowerShell*') { $null = $info.Environment.Remove('PSModulePath') }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $writer = [IO.StreamWriter]::new($Log, $true)
    $writer.AutoFlush = $true
    $handler = { if ($null -ne $EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) } }
    $null = Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -Action $handler -MessageData $writer
    $null = Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -Action $handler -MessageData $writer
    $null = $process.Start()
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $owned.Add($process)
    $process
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Seconds = 180) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $value = & $Condition; if ($value) { return $value } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for: $Description"
}

function Get-InstalledVersion {
    (Invoke-RestMethod -Uri "http://127.0.0.1:$localPort/api/version" -TimeoutSec 2)
}

try {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $localPort)
    try { $probe.Start() } catch { throw "Port $localPort is in use; close the installed Meld Research first. Nothing was stopped." } finally { $probe.Stop() }

    Write-Host '==> Throwaway signing key and two synthetic releases'
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $publicKey = [IO.Path]::Combine($temp, 'update-public-key.txt')
    [IO.File]::WriteAllText($publicKey, [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start()
    $remotePort = $listener.LocalEndpoint.Port; $listener.Stop()
    $remoteUrl = "http://127.0.0.1:$remotePort"
    $older = [pscustomobject]@{ Version = 'a' * 64; Date = [DateTimeOffset]::UtcNow.AddHours(-1) }
    $newer = [pscustomobject]@{ Version = 'b' * 64; Date = [DateTimeOffset]::UtcNow }

    & npm --prefix ([IO.Path]::Combine($root, 'apps', 'frontend')) run build
    if ($LASTEXITCODE -ne 0) { throw 'The frontend build failed.' }
    foreach ($release in @($older, $newer)) {
        & ([IO.Path]::Combine($root, 'tools', 'package-local.ps1')) -Runtime win-x64 -ClientVersion $release.Version `
            -ClientVersionDate $release.Date -OutputDirectory ([IO.Path]::Combine($temp, $release.Version.Substring(0, 1))) `
            -RemoteUrl $remoteUrl -UpdatePublicKeyPath $publicKey -SkipFrontendBuild
        $release | Add-Member Package ([IO.Path]::Combine($temp, $release.Version.Substring(0, 1), 'meldresearch-win-x64.zip'))
    }

    Write-Host '==> Remote backend publishing the newer release'
    $downloads = [IO.Directory]::CreateDirectory([IO.Path]::Combine($temp, 'downloads')).FullName
    function Publish($Release) {
        Copy-Item -LiteralPath $Release.Package -Destination ([IO.Path]::Combine($downloads, 'meldresearch-win-x64.zip')) -Force
        New-SignedUpdate -Key $key -Runtime win-x64 -Version $Release.Version -VersionDate $Release.Date -PackagePath $Release.Package `
            -Destination ([IO.Path]::Combine($downloads, 'meldresearch-win-x64.update.json'))
    }
    Publish $older
    $remoteExe = [IO.Path]::Combine($root, 'apps', 'RemoteBackend', 'bin', 'Debug', 'net10.0', 'MeldResearch.RemoteBackend.exe')
    $null = Start-Owned $remoteExe @('--Port', $remotePort, '--PublicOrigin', $remoteUrl, '--Dummy', 'true', '--environment', 'Development',
        '--DataDirectory', ([IO.Path]::Combine($temp, 'remote')), '--DownloadDirectory', $downloads) @{} ([IO.Path]::Combine($temp, 'remote.log'))
    $null = Wait-Until { (Invoke-WebRequest "$remoteUrl/api/updates/win-x64" -TimeoutSec 2).StatusCode -eq 200 } 'remote update manifest' 60

    Write-Host '==> Installing the published older release with the one-line command (Windows PowerShell 5.1)'
    $environment = @{
        LOCALAPPDATA = [IO.Path]::Combine($temp, 'localappdata'); APPDATA = [IO.Path]::Combine($temp, 'appdata')
        MELDRESEARCH_DATA_DIR = [IO.Path]::Combine($temp, 'data'); ASPNETCORE_ENVIRONMENT = 'Development'
        MELDRESEARCH_Updates__RegisterNativeHost = 'false'; MELDRESEARCH_NO_BROWSER = 'true'; MELDRESEARCH_Install__Autostart = 'false'
        MELDRESEARCH_Updates__InitialDelaySeconds = '3'; MELDRESEARCH_Updates__IntervalSeconds = '5'
    }
    foreach ($directory in @($environment.LOCALAPPDATA, [IO.Path]::Combine($environment.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs'))) {
        $null = [IO.Directory]::CreateDirectory($directory)
    }
    $launcherLog = [IO.Path]::Combine($temp, 'launcher.log')
    $oneLiner = "irm -useb $remoteUrl/api/install/windows | iex"
    $null = Start-Owned "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @('-NoProfile', '-Command', $oneLiner) $environment $launcherLog
    $app = [IO.Path]::Combine($environment.LOCALAPPDATA, 'MeldResearch', 'App')

    $first = Wait-Until { $v = Get-InstalledVersion; if ($v.version -eq $older.Version) { $v } } 'the installed application' 180
    Write-Host "    started $($first.version.Substring(0, 12)) (updates enabled: $($first.updatesEnabled))"
    if (-not $first.updatesEnabled) { throw "Automatic updates are disabled: $($first.updatesDisabledReason)" }
    foreach ($step in @('STEP 2 / 3', 'length and SHA-256 match the release manifest', "Installing release $($older.Version.Substring(0, 12))")) {
        if (-not (Select-String -LiteralPath $launcherLog -SimpleMatch $step -Quiet)) { throw "The installer output lacks: $step" }
    }

    Write-Host '==> Publishing the newer release; waiting for the automatic update and launcher restart'
    Publish $newer
    $updated = Wait-Until { $v = Get-InstalledVersion; if ($v.version -eq $newer.Version) { $v } } 'the updated application' 240
    $pointer = Get-Content -LiteralPath ([IO.Path]::Combine($app, 'current.json')) -Raw | ConvertFrom-Json
    if ($pointer.current -ne $newer.Version.Substring(0, 12) -or $pointer.previous -ne $older.Version.Substring(0, 12)) {
        throw "Unexpected installation pointer: $($pointer | ConvertTo-Json -Compress)"
    }
    if ((Get-FileHash ([IO.Path]::Combine($app, 'Start-Meld.ps1'))).Hash -ne
        (Get-FileHash ([IO.Path]::Combine($app, $pointer.current, 'Start-Meld.ps1'))).Hash) { throw 'The stable launcher was not refreshed.' }
    if (-not $updated.updatesEnabled) { throw "The updated version disabled updates: $($updated.updatesDisabledReason)" }
    $shortcut = [IO.Path]::Combine($environment.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Meld Research.lnk')
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'The Start menu shortcut was not created.' }
    if (-not (Select-String -LiteralPath $launcherLog -SimpleMatch 'Restarting Meld Research into the installed update' -Quiet)) {
        throw 'The launcher did not report the update restart.'
    }
    Write-Host "    now running $($updated.version.Substring(0, 12)); pointer previous = $($pointer.previous)"

    Write-Host '==> Publishing an older signed release, which must be ignored'
    Publish $older
    Start-Sleep -Seconds 15
    $after = Get-InstalledVersion
    if ($after.version -ne $newer.Version) { throw 'An older signed release was installed (downgrade).' }
    if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($app, $older.Version.Substring(0, 12))))) { throw 'The previous version folder was removed too early.' }
    Write-Host "    still $($after.version.Substring(0, 12)); update status: $($after.updateStatus)"

    Write-Host '==> Re-running the one-line command for the installed release (repair)'
    Publish $newer
    $repairLog = [IO.Path]::Combine($temp, 'repair.log')
    $null = Start-Owned "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @('-NoProfile', '-Command', $oneLiner) $environment $repairLog
    $null = Wait-Until { Select-String -LiteralPath $repairLog -SimpleMatch 'already installed; reusing its files' -Quiet } 'the repair install' 120
    $repaired = Wait-Until { $v = Get-InstalledVersion; if ($v.version -eq $newer.Version) { $v } } 'the repaired application' 120
    $pointer = Get-Content -LiteralPath ([IO.Path]::Combine($app, 'current.json')) -Raw | ConvertFrom-Json
    if ($pointer.current -ne $newer.Version.Substring(0, 12) -or $pointer.previous -ne $older.Version.Substring(0, 12)) {
        throw "The repair changed the rollback target: $($pointer | ConvertTo-Json -Compress)"
    }
    Write-Host "    running $($repaired.version.Substring(0, 12)) again; previous version still $($pointer.previous)"
    Write-Host 'Desktop update E2E passed: one-line install, signed automatic update, launcher restart, stable launcher refresh, downgrade refused, repair re-run.' -ForegroundColor Green
}
finally {
    $listening = Get-NetTCPConnection -State Listen -LocalPort $localPort -ErrorAction SilentlyContinue
    foreach ($connection in @($listening)) {
        $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
        if ($process -and $process.Path -and $process.Path.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $process.Id -Force }
    }
    foreach ($process in $owned) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit(10000) | Out-Null }
    }
    Get-EventSubscriber | Unregister-Event -ErrorAction SilentlyContinue
    if ($key) { $key.Dispose() }
    if ($KeepTemporary) { Write-Host "Kept $temp" }
    else { Start-Sleep -Seconds 1; Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
