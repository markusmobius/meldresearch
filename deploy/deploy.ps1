#requires -Version 7.0
<#
.SYNOPSIS
Builds and deploys Meld Research on the Ubuntu server, from the repository clone (README: "Releases").

.DESCRIPTION
Run as yourself (the user the service runs as, not root) in ~/meldresearch after `git pull`, normally via
deploy/deploy.sh. One run:
1. Rebuilds each desktop package (win-x64, osx-arm64, osx-x64; cross-built here) only when the committed desktop
   client inputs changed, and signs it with your passphrase-protected update key (prompted only then).
2. Rebuilds the remote backend.
3. Copies it to <repo>/deployed/releases/<stamp>.<id>, points <repo>/deployed/current at it, restarts the service
   (sudo systemctl) and health-checks it, rolling back to the previous release if it is unhealthy.
4. Publishes new desktop releases to ~/meldresearch-data/downloads, from which installed clients update themselves.
Nothing here installs nginx, certificates or the systemd unit (README: "Server setup").

.EXAMPLE
pwsh deploy/deploy.ps1
pwsh deploy/deploy.ps1 -FoundrySmokeTest
pwsh deploy/deploy.ps1 -SkipBuild        # redeploy the last build
pwsh deploy/deploy.ps1 -BuildOnly        # build only (also works on Windows)
#>
[CmdletBinding()]
param(
    # One or more of win-x64, osx-arm64, osx-x64 (comma-separated values work from bash too).
    [string[]]$DesktopRuntimes = @('win-x64', 'osx-arm64', 'osx-x64'),
    # Rebuild desktop packages even when their committed inputs did not change.
    [switch]$ForceDesktopBuild,
    # Build into artifacts/ without deploying.
    [switch]$BuildOnly,
    # Deploy the existing build in artifacts/ without rebuilding.
    [switch]$SkipBuild,
    # After deploying, run the live Foundry smoke test with the service's configuration (consumes a little quota).
    [switch]$FoundrySmokeTest,
    [string]$ExpectFoundryEndpoint = 'https://meldllm.openai.azure.com/openai/v1/'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. ([IO.Path]::Combine($root, 'tools', 'release-common.ps1'))
if ($BuildOnly -and $SkipBuild) { throw 'Use either -BuildOnly or -SkipBuild.' }
if ($ExpectFoundryEndpoint -notmatch '^https://[A-Za-z0-9.-]+/openai/v1/?$') { throw 'ExpectFoundryEndpoint must be an https://<host>/openai/v1/ URL.' }
$DesktopRuntimes = @($DesktopRuntimes | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
foreach ($desktopRuntime in $DesktopRuntimes) {
    if ($desktopRuntime -notin @('win-x64', 'osx-arm64', 'osx-x64')) { throw "Unknown desktop runtime '$desktopRuntime'. Use win-x64, osx-arm64 or osx-x64." }
}
if (-not $BuildOnly) {
    if (-not $IsLinux) { throw 'Deploy on the Ubuntu server. Elsewhere, use -BuildOnly.' }
    if ((& id -u) -eq '0') { throw 'Run as yourself, not root; the deploy uses sudo only to restart the service.' }
}
$Runtime = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'Arm64' { 'linux-arm64' }
    default { 'linux-x64' }
}
$frontend = [IO.Path]::Combine($root, 'apps', 'frontend')
$desktop = [IO.Path]::Combine($root, 'artifacts', 'desktop')
$publish = [IO.Path]::Combine($root, 'artifacts', 'remote', "publish-$Runtime")
$deployed = [IO.Path]::Combine($root, 'deployed')
$home_ = [Environment]::GetFolderPath('UserProfile')
$downloads = if ($env:MELDRESEARCH_DOWNLOADS) { $env:MELDRESEARCH_DOWNLOADS } else { [IO.Path]::Combine($home_, 'meldresearch-data', 'downloads') }
$healthUrl = if ($env:MELDRESEARCH_HEALTH_URL) { $env:MELDRESEARCH_HEALTH_URL } else { 'http://127.0.0.1:8080/healthz' }
$service = 'meldresearch-remote'
$keepReleases = 5

function Invoke-Systemctl([string[]]$Arguments, [switch]$Quiet) {
    # Isolated tests substitute a stand-in; real deploys use sudo systemctl.
    if ($env:MELDRESEARCH_SYSTEMCTL) { $exe = $env:MELDRESEARCH_SYSTEMCTL; $all = $Arguments }
    else { $exe = 'sudo'; $all = @('systemctl') + $Arguments }
    if ($Quiet) { & $exe @all *> $null } else { & $exe @all | Out-Host }
    $LASTEXITCODE -eq 0
}

$frontendBuilt = $false
function Build-Frontend {
    if ($script:frontendBuilt) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $frontend 'node_modules'))) {
        & npm --prefix $frontend ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    }
    & npm --prefix $frontend run build
    if ($LASTEXITCODE -ne 0) { throw 'The Angular build failed.' }
    $script:frontendBuilt = $true
}

if (-not $SkipBuild) {
    $null = [IO.Directory]::CreateDirectory($desktop)
    # 1. Desktop client: rebuild and sign only what changed.
    $release = Get-ClientRelease -Root $root
    $short = $release.Version.Substring(0, 12)
    Write-Host "Desktop client release $short ($($release.VersionDate.ToString('u')))" -ForegroundColor Cyan
    $stale = @($DesktopRuntimes | Where-Object {
        $package = [IO.Path]::Combine($desktop, "meldresearch-$_.zip"); $signed = [IO.Path]::Combine($desktop, "meldresearch-$_.update.json")
        if ($ForceDesktopBuild -or -not (Test-Path -LiteralPath $package) -or -not (Test-Path -LiteralPath $signed)) { return $true }
        $existing = Read-SignedUpdate $signed
        $existing.version -ne $release.Version -or
            (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $existing.sha256
    })
    if ($stale.Count -gt 0) {
        # Unlock the key before the long build, so a wrong passphrase fails immediately.
        $key = Open-UpdateSigningKey -Root $root
        try {
            Build-Frontend
            foreach ($desktopRuntime in $stale) {
                & ([IO.Path]::Combine($root, 'tools', 'package-local.ps1')) -Runtime $desktopRuntime -ClientVersion $release.Version `
                    -ClientVersionDate $release.VersionDate -OutputDirectory $desktop -SkipFrontendBuild
                $package = [IO.Path]::Combine($desktop, "meldresearch-$desktopRuntime.zip")
                New-SignedUpdate -Key $key -Runtime $desktopRuntime -Version $release.Version -VersionDate $release.VersionDate `
                    -PackagePath $package -Destination ([IO.Path]::Combine($desktop, "meldresearch-$desktopRuntime.update.json"))
                Write-Host "  ${desktopRuntime}: built and signed release $short"
            }
        }
        finally { $key.Dispose() }
    }
    foreach ($desktopRuntime in $DesktopRuntimes | Where-Object { $_ -notin $stale }) { Write-Host "  ${desktopRuntime}: unchanged, reusing release $short" }

    # 2. Remote backend: always rebuilt.
    Build-Frontend
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    & dotnet publish ([IO.Path]::Combine($root, 'apps', 'RemoteBackend', 'MeldResearch.RemoteBackend.csproj')) -c Release -r $Runtime `
        --self-contained true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The remote backend publish failed.' }
}
foreach ($required in @('MeldResearch.RemoteBackend', 'wwwroot/theme.css', 'wwwroot/privacy.html', 'wwwroot/docs.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "The remote backend build is missing $required. Run without -SkipBuild." }
}
foreach ($desktopRuntime in $DesktopRuntimes) {
    foreach ($file in "meldresearch-$desktopRuntime.zip", "meldresearch-$desktopRuntime.update.json") {
        if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($desktop, $file)))) { throw "The desktop build is missing $file. Run without -SkipBuild." }
    }
}
if ($BuildOnly) { Write-Host "Built in $([IO.Path]::Combine($root, 'artifacts')); nothing was deployed."; return }

# 3. Remote backend: copy into a new release folder, switch `current` in one step, restart, health-check.
if (-not (Invoke-Systemctl @('cat', $service) -Quiet)) { throw "The systemd unit $service is not installed. Complete the one-time server setup first." }
$releases = [IO.Directory]::CreateDirectory([IO.Path]::Combine($deployed, 'releases')).FullName
$target = [IO.Path]::Combine($releases, "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$([Guid]::NewGuid().ToString('N').Substring(0, 4))")
Write-Host "==> Installing release $(Split-Path -Leaf $target)"
Copy-Item -LiteralPath $publish -Destination $target -Recurse
foreach ($executable in 'MeldResearch.RemoteBackend', 'createdump') {
    $path = [IO.Path]::Combine($target, $executable)
    if (Test-Path -LiteralPath $path) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute') }
}
$current = [IO.Path]::Combine($deployed, 'current')
$previous = if (Test-Path -LiteralPath $current) { (Get-Item -LiteralPath $current).ResolveLinkTarget($true).FullName } else { $null }
function Switch-Current([string]$To) {
    & ln -sfn $To "$current.new"
    & mv -Tf "$current.new" $current
    if ($LASTEXITCODE -ne 0) { throw 'Switching the current release failed.' }
}
function Test-Healthy {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { if ((Invoke-WebRequest -Uri $healthUrl -TimeoutSec 2).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 1
    }
    $false
}
Switch-Current $target
Write-Host "==> Restarting $service"
$null = Invoke-Systemctl @('restart', $service)
if (-not (Test-Healthy)) {
    $null = Invoke-Systemctl @('status', $service, '--no-pager', '-n', '40')
    if ($previous -and (Test-Path -LiteralPath $previous) -and $previous -ne $target) {
        Write-Warning "The new release is not healthy; rolling back to $(Split-Path -Leaf $previous)."
        Switch-Current $previous
        $null = Invoke-Systemctl @('restart', $service)
    }
    throw "The new release did not become healthy at $healthUrl. No desktop update was published."
}

# 4. Desktop releases: publish only after the backend that serves them is healthy, and only when changed.
#    Each package is replaced before its signed manifest, so clients never see a manifest naming a missing package.
$null = [IO.Directory]::CreateDirectory($downloads)
[IO.File]::SetUnixFileMode($downloads, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute')
foreach ($desktopRuntime in $DesktopRuntimes) {
    $files = "meldresearch-$desktopRuntime.zip", "meldresearch-$desktopRuntime.update.json"
    $unchanged = @($files | Where-Object {
        $live = [IO.Path]::Combine($downloads, $_)
        (Test-Path -LiteralPath $live) -and
            (Get-FileHash -LiteralPath $live).Hash -eq (Get-FileHash -LiteralPath ([IO.Path]::Combine($desktop, $_))).Hash
    }).Count -eq 2
    if ($unchanged) { Write-Host "==> Desktop release for $desktopRuntime is already published"; continue }
    foreach ($file in $files) {
        $partial = [IO.Path]::Combine($downloads, ".$file.partial")
        Copy-Item -LiteralPath ([IO.Path]::Combine($desktop, $file)) -Destination $partial -Force
        [IO.File]::SetUnixFileMode($partial, [IO.UnixFileMode]'UserRead, UserWrite')
        Move-Item -LiteralPath $partial -Destination ([IO.Path]::Combine($downloads, $file)) -Force
    }
    Write-Host "==> Published desktop release for $desktopRuntime"
}

# Keep the newest releases (and always the current one).
$live = (Get-Item -LiteralPath $current).ResolveLinkTarget($true).FullName
Get-ChildItem -LiteralPath $releases -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $keepReleases |
    Where-Object { $_.FullName -ne $live } | Remove-Item -Recurse -Force
Write-Host "==> Deployed $(Split-Path -Leaf $target)" -ForegroundColor Green

if ($FoundrySmokeTest) {
    # Same user, configuration and managed identity as the service.
    Write-Host '==> Foundry smoke test (service configuration, managed identity)'
    & sudo systemd-run --wait --pipe --quiet --collect -p "User=$(& id -un)" `
        -p "EnvironmentFile=$([IO.Path]::Combine($home_, 'meldresearch-data', 'remote.env'))" -p Environment=ASPNETCORE_ENVIRONMENT=Production `
        ([IO.Path]::Combine($current, 'MeldResearch.RemoteBackend')) --foundry-smoke-test --expect-endpoint $ExpectFoundryEndpoint
    if ($LASTEXITCODE -ne 0) { throw 'The Foundry smoke test failed.' }
}
