#requires -Version 7.0
[CmdletBinding()]
param([string]$ArchivePath = [IO.Path]::Combine($PSScriptRoot, '..', 'artifacts', 'desktop', 'meldresearch-win-x64.zip'))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This check executes the Windows package and must run on Windows.' }
$temporary = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'meldresearch-package-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temporary)
$backend = $null
$hostProcess = $null

function Read-NativeBytes([IO.Stream]$Stream, [int]$Count) {
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.ReadAsync($buffer, $offset, $Count - $offset)
        if (-not $read.Wait(10000)) { throw 'The published native host did not respond in time.' }
        $countRead = $read.GetAwaiter().GetResult()
        if ($countRead -eq 0) { throw 'The published native host stopped before sending a complete message.' }
        $offset += $countRead
    }
    return ,$buffer
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ArchivePath), $temporary)
    $metadata = Get-Content -LiteralPath ([IO.Path]::Combine($temporary, 'installation.json')) -Raw | ConvertFrom-Json
    if ($metadata.ExtensionId -ne 'pejcljoimoeklelcfbfddnkimlkjgfhe' -or $metadata.ConnectorVersion -ne '0.3.0' -or
        $metadata.Version -notmatch '^[0-9a-f]{64}$' -or $metadata.Runtime -ne 'win-x64') {
        throw 'The packaged public identity, connector version or release identity is wrong.'
    }
    foreach ($name in @('local', 'browser-host')) {
        $assembly = if ($name -eq 'local') { 'LocalBackend' } else { 'BrowserHost' }
        $runtime = Get-Content -LiteralPath ([IO.Path]::Combine($temporary, $name, "MeldResearch.$assembly.runtimeconfig.json")) -Raw | ConvertFrom-Json
        if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework) {
            throw 'The package is not self-contained.'
        }
    }
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::Combine($temporary, 'local', 'MeldResearch.LocalBackend.exe'))
    $start.WorkingDirectory = [IO.Path]::Combine($temporary, 'local')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['MELDRESEARCH_DATA_DIR'] = [IO.Path]::Combine($temporary, 'data')
    $start.Environment['DOTNET_ROOT'] = [IO.Path]::Combine($temporary, 'no-installed-dotnet')
    foreach ($argument in @('--Port', "$port", '--FrontendPath', [IO.Path]::Combine($temporary, 'frontend'),
        '--ConnectorDirectory', [IO.Path]::Combine($temporary, 'browser', 'overleaf'))) { $start.ArgumentList.Add($argument) }
    $backend = [Diagnostics.Process]::Start($start)
    $stdout = $backend.StandardOutput.ReadToEndAsync()
    $stderr = $backend.StandardError.ReadToEndAsync()
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($backend.HasExited) { throw 'The published backend failed to start; no installed application was touched.' }
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/status" -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch [System.Net.Http.HttpRequestException] { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'The published backend did not become responsive.' }
    $setup = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/extension"
    if ($setup.extensionId -ne $metadata.ExtensionId -or $setup.developmentDirectory -ne [IO.Path]::Combine($temporary, 'browser', 'overleaf')) {
        throw 'The published backend did not expose its bundled connector identity and installation directory.'
    }
    $page = Invoke-WebRequest -Uri "http://127.0.0.1:$port"
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch '<app-root') { throw 'The published Angular application was not served.' }
    $nativeStart = [Diagnostics.ProcessStartInfo]::new([IO.Path]::Combine($temporary, 'browser-host', 'MeldResearch.BrowserHost.exe'))
    $nativeStart.UseShellExecute = $false
    $nativeStart.RedirectStandardInput = $true
    $nativeStart.RedirectStandardOutput = $true
    $nativeStart.RedirectStandardError = $true
    $nativeStart.Environment['MELDRESEARCH_DATA_DIR'] = $start.Environment['MELDRESEARCH_DATA_DIR']
    $nativeStart.Environment['DOTNET_ROOT'] = $start.Environment['DOTNET_ROOT']
    $hostProcess = [Diagnostics.Process]::Start($nativeStart)
    $nativeError = $hostProcess.StandardError.ReadToEndAsync()
    $prefix = Read-NativeBytes $hostProcess.StandardOutput.BaseStream 4
    $length = [BitConverter]::ToInt32($prefix, 0)
    if ($length -le 0 -or $length -gt 262144) { throw 'The native host returned invalid framing.' }
    $bytes = Read-NativeBytes $hostProcess.StandardOutput.BaseStream $length
    $message = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if ($message.type -ne 'ready' -or $message.version -ne 2) { throw 'The self-contained native host did not authenticate to the self-contained backend.' }
    $hostProcess.StandardInput.Close()
    if (-not $hostProcess.WaitForExit(10000)) { throw 'The native host failed to stop after its owning connection ended.' }
    Write-Host 'PASS: self-contained Windows backend + Angular + published native-host handshake; correct Chrome identity and guided-install folder.'
    Write-Host 'No installer, browser registration, browser profile, Google login, or real Overleaf account was used.'
}
finally {
    foreach ($process in @($hostProcess, $backend)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
            $process.Dispose()
        }
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
