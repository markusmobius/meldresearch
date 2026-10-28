#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 6001)
try { $probe.Start() }
catch [Net.Sockets.SocketException] { throw 'Port 6001 is occupied. No process was stopped. Close the existing application before starting another.' }
finally { $probe.Stop() }
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Raw | ConvertFrom-Json
$process = Start-Process -FilePath (Join-Path $PSScriptRoot 'local\MeldResearch.LocalBackend.exe') `
    -WorkingDirectory (Join-Path $PSScriptRoot 'local') `
    -ArgumentList @('--Port', '6001', '--FrontendPath', ('"' + (Join-Path $PSScriptRoot 'frontend') + '"'),
        '--ConnectorDirectory', ('"' + (Join-Path $PSScriptRoot 'browser\overleaf') + '"')) -PassThru -NoNewWindow
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw 'Meld failed to start. Review the message in this terminal.' }
        try { $null = Invoke-WebRequest -Uri 'http://127.0.0.1:6001/api/status' -UseBasicParsing -TimeoutSec 2; $ready = $true; break }
        catch [System.Net.WebException] { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Meld did not become ready.' }
    if ($metadata.StorePublished) {
        if ($metadata.ExtensionId -notmatch '^[a-p]{32}$') { throw 'Invalid connector identity.' }
        Start-Process ('https://chromewebstore.google.com/detail/' + $metadata.ExtensionId)
    }
    Start-Process 'http://127.0.0.1:6001'
    Write-Host 'Link your existing Google-based Meld account, install/approve the connector, then select projects and consent to Foundry processing.'
    Write-Host 'Keep this terminal open. Ctrl+C stops this owned application instance.'
    $process.WaitForExit()
}
finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }; $process.Dispose() }
