#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Dummy', 'Live')]
    [string]$Mode = 'Dummy',
    [switch]$SkipBuild,
    [switch]$Check,
    [string]$RemoteUrl,
    [string]$FoundrySettings,
    [ValidateSet(6001, 6010)]
    [int]$Port = 6010
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$frontend = [IO.Path]::Combine($PSScriptRoot, 'apps', 'frontend')
$frontendBuild = [IO.Path]::Combine($frontend, 'dist', 'frontend', 'browser')
$suffix = if ($IsWindows) { '.exe' } else { '' }
$backend = [IO.Path]::Combine([string[]]@($PSScriptRoot, 'apps', 'LocalBackend', 'bin', 'Debug', 'net10.0', "MeldResearch.LocalBackend$suffix"))
$nativeHost = [IO.Path]::Combine([string[]]@($PSScriptRoot, 'apps', 'BrowserHost', 'bin', 'Debug', 'net10.0', "MeldResearch.BrowserHost$suffix"))
$remoteBackend = [IO.Path]::Combine([string[]]@($PSScriptRoot, 'apps', 'RemoteBackend', 'bin', 'Debug', 'net10.0', "MeldResearch.RemoteBackend$suffix"))
if (-not $FoundrySettings) { $FoundrySettings = [IO.Path]::Combine($PSScriptRoot, '.local-test', 'Foundry.Local.json') }

if (-not $SkipBuild) {
    if (-not (Test-Path -LiteralPath (Join-Path $frontend 'node_modules'))) {
        & npm --prefix $frontend ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    }
    & npm --prefix $frontend run build
    if ($LASTEXITCODE -ne 0) { throw 'The Angular build failed.' }
    & dotnet build (Join-Path $PSScriptRoot 'MeldResearch.slnx') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The C# build failed.' }
}

if (-not (Test-Path -LiteralPath $backend) -or -not (Test-Path -LiteralPath $nativeHost) -or
    -not (Test-Path -LiteralPath (Join-Path $frontendBuild 'index.html'))) {
    throw 'Build outputs are missing. Run without -SkipBuild.'
}

if ($Check) {
    & dotnet test ([IO.Path]::Combine($PSScriptRoot, 'tests', 'MeldResearch.Tests', 'MeldResearch.Tests.csproj')) --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The C# tests failed.' }
    & node --test ([IO.Path]::Combine($PSScriptRoot, 'tests', 'extension.test.mjs')) ([IO.Path]::Combine($PSScriptRoot, 'tests', 'chrome-package.test.mjs'))
    if ($LASTEXITCODE -ne 0) { throw 'The browser extension or store-package tests failed.' }
    & npm --prefix $frontend test
    if ($LASTEXITCODE -ne 0) { throw 'The Angular tests failed.' }
    & node ([IO.Path]::Combine($PSScriptRoot, 'tests', 'overleaf.e2e.mjs'))
    if ($LASTEXITCODE -ne 0) { throw 'The synthetic end-to-end check failed.' }
    if (-not (Test-Path -LiteralPath $FoundrySettings)) { throw 'Create .local-test\Foundry.Local.json (README: "Run the full development stack") before the real-inference check.' }
    & node ([IO.Path]::Combine($PSScriptRoot, 'tests', 'research.e2e.mjs')) --foundry-settings $FoundrySettings
    if ($LASTEXITCODE -ne 0) { throw 'The real-Foundry research end-to-end check failed.' }
    return
}

$startRemote = [string]::IsNullOrWhiteSpace($RemoteUrl)
if ($startRemote) { $RemoteUrl = 'http://127.0.0.1:6050' }
if ($Mode -eq 'Dummy' -and -not $startRemote) { throw 'Dummy mode starts its own isolated remote account service on port 6050.' }
foreach ($requiredPort in $(if ($startRemote) { @($Port, 6050) } else { @($Port) })) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $requiredPort)
    try { $probe.Start() }
    catch [Net.Sockets.SocketException] { throw "Port $requiredPort is occupied. No existing process was stopped." }
    finally { $probe.Stop() }
}
$modeRoot = [IO.Path]::Combine($PSScriptRoot, '.local-test', $Mode)
$logs = [IO.Path]::Combine($modeRoot, 'logs')
$null = [IO.Directory]::CreateDirectory($logs)
$dummy = if ($Mode -eq 'Dummy') { 'true' } else { 'false' }
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$oldData = $env:MELDRESEARCH_DATA_DIR

function Wait-Ready([Diagnostics.Process]$Process, [string]$Url) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw 'An owned backend stopped during startup. See its log files; no other process was stopped.' }
        try {
            $response = Invoke-WebRequest -Uri $Url -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        }
        catch [System.Net.Http.HttpRequestException] { Start-Sleep -Milliseconds 200 }
        catch [System.Threading.Tasks.TaskCanceledException] { Start-Sleep -Milliseconds 200 }
    }
    throw 'The backend did not become ready. See the mode-specific log files.'
}

try {
    if ($startRemote) {
        $arguments = @('--Port', '6050', '--PublicOrigin', $RemoteUrl, '--Dummy', $dummy,
            '--DataDirectory', "`"$([IO.Path]::Combine($modeRoot, 'remote'))`"", '--environment', 'Development')
        if (Test-Path -LiteralPath $FoundrySettings) { $arguments += @('--SettingsFile', "`"$FoundrySettings`"") }
        else { Write-Warning 'Foundry is not configured. Project processing will fail explicitly until configured and restarted.' }
        $remoteProcess = Start-Process -FilePath $remoteBackend -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput ([IO.Path]::Combine($logs, 'remote.stdout.log')) `
            -RedirectStandardError ([IO.Path]::Combine($logs, 'remote.stderr.log'))
        $owned.Add($remoteProcess)
        Wait-Ready $remoteProcess "$RemoteUrl/api/status"
    }
    if ($Mode -eq 'Dummy') { $env:MELDRESEARCH_DATA_DIR = [IO.Path]::Combine($modeRoot, 'local') }
    else {
        & $nativeHost --register
        if ($LASTEXITCODE -ne 0) { throw 'The per-user native messaging registration failed.' }
    }
    $arguments = @('--Port', $Port, '--FrontendPath', "`"$frontendBuild`"", '--RemoteUrl', $RemoteUrl,
        '--Dummy', $dummy, '--environment', 'Development')
    $localProcess = Start-Process -FilePath $backend -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput ([IO.Path]::Combine($logs, 'local.stdout.log')) `
        -RedirectStandardError ([IO.Path]::Combine($logs, 'local.stderr.log'))
    $owned.Add($localProcess)
    Wait-Ready $localProcess "http://127.0.0.1:$Port/api/status"
    Write-Host "Local application: http://127.0.0.1:$Port"
    Write-Host "Remote service and privacy page: $RemoteUrl"
    Write-Host "Mode: $Mode. Logs: $logs"
    if ($Mode -eq 'Dummy') { Write-Host 'Synthetic account and Overleaf evidence; real configured Foundry inference. No real browser extension is required.' }
    else { Write-Host 'Real Overleaf. Reload the connector once for the store identity change. Google account linking and model-processing consent are required.' }
    Write-Host 'No research is monitored until you link an account, approve model processing, and select projects.'
    Write-Host 'Keep this terminal open. Ctrl+C stops only these owned backend processes.'
    while (-not $localProcess.HasExited -and (-not $startRemote -or -not $remoteProcess.HasExited)) { Start-Sleep -Seconds 1 }
    throw 'A backend stopped. Check the mode-specific logs.'
}
finally {
    $env:MELDRESEARCH_DATA_DIR = $oldData
    foreach ($process in $owned) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        $process.Dispose()
    }
}
