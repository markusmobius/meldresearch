# Meld Research -- installer for Windows (per user; no administrator rights needed).
#   irm -useb {{REMOTEURL}}/api/install/windows | iex
# Downloads the signed desktop release published by {{REMOTEURL}}, checks its length and SHA-256 against the
# release manifest, and runs the package's own installer. That installer adds the release to
# %LOCALAPPDATA%\MeldResearch\App, starts it in the background at every sign-in, and opens the local app.
# Running this again repairs or updates an existing installation; research data is kept.
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $remote = '{{REMOTEURL}}'
    $runtime = 'win-x64'
    $totalSteps = 3
    function Step([int]$Number, [string]$Title) {
        Write-Host ''
        Write-Host '==========================================================================' -ForegroundColor White
        Write-Host "  STEP $Number / $totalSteps  --  $Title" -ForegroundColor Cyan
        Write-Host '==========================================================================' -ForegroundColor White
    }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('meldresearch-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        Step 1 'Check this computer'
        if (-not [Environment]::Is64BitOperatingSystem) { throw 'Meld Research requires 64-bit Windows.' }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Write-Host '  64-bit Windows. The package is self-contained: no .NET installation is needed.' -ForegroundColor Green

        Step 2 'Download and verify the signed release'
        $null = New-Item -ItemType Directory -Path $work
        $envelope = Invoke-RestMethod -Uri "$remote/api/updates/$runtime" -UseBasicParsing
        $manifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$envelope.manifest)) | ConvertFrom-Json
        if ($manifest.runtime -ne $runtime -or [string]$manifest.version -notmatch '^[0-9a-f]{64}$' -or
            [string]$manifest.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'The server returned an invalid release manifest.' }
        Write-Host "  Release $(([string]$manifest.version).Substring(0, 12)) ($($manifest.versionDate))" -ForegroundColor Cyan
        $zip = Join-Path $work "meldresearch-$runtime.zip"
        Invoke-WebRequest -Uri "$remote/api/updates/$runtime/package/$($manifest.version)" -OutFile $zip -UseBasicParsing
        $length = (Get-Item -LiteralPath $zip).Length
        $stream = [IO.File]::OpenRead($zip)
        try { $hash = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
        finally { $stream.Dispose() }
        if ($length -ne [long]$manifest.length -or $hash -ne $manifest.sha256) {
            throw 'The downloaded package does not match the release manifest. Nothing was installed.'
        }
        Write-Host "  Downloaded $([Math]::Round($length / 1MB, 1)) MB; length and SHA-256 match the release manifest." -ForegroundColor Green

        Step 3 'Install and start at sign-in'
        $package = Join-Path $work 'package'
        Expand-Archive -LiteralPath $zip -DestinationPath $package
        # The installer runs in Windows PowerShell 5.1; PowerShell 7's module path would hide its built-in modules.
        $modulePath = $env:PSModulePath
        if ($PSVersionTable.PSEdition -eq 'Core') { $env:PSModulePath = $null }
        try {
            & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-Meld.ps1')
        }
        finally { $env:PSModulePath = $modulePath }
        if ($LASTEXITCODE -ne 0) { throw 'The package installer reported an error (see above).' }
    }
    catch {
        Write-Host ''
        Write-Host "  [FAIL]  $($_.Exception.Message)" -ForegroundColor Red
        Write-Host '          Installation did not complete. Please report the step number above.' -ForegroundColor Red
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
