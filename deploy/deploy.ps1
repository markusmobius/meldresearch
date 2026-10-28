#requires -Version 7.0
<#
.SYNOPSIS
Builds and deploys a release on the Ubuntu server, from a clone of this repository (README: "Releases").

.DESCRIPTION
Run as yourself (the user the service runs as, not root) from the repository clone after `git pull`:
- The remote backend is always rebuilt and deployed to <repo>/deployed (git-ignored) by apply-release.sh,
  which uses sudo only to restart the service.
- Each desktop package (win-x64, osx-arm64, osx-x64; cross-built here) is rebuilt only when the committed
  desktop client inputs change, then signed with your passphrase-protected update key (prompted only then)
  and published to ~/meldresearch-data/downloads. Installed desktop clients update themselves from it.
- Nothing here installs nginx, certificates or the systemd unit.

.EXAMPLE
git pull --ff-only; pwsh deploy/deploy.ps1
pwsh deploy/deploy.ps1 -FoundrySmokeTest
pwsh deploy/deploy.ps1 -BuildOnly
#>
[CmdletBinding()]
param(
    # One or more of win-x64, osx-arm64, osx-x64 (comma-separated values work from bash too).
    [string[]]$DesktopRuntimes = @('win-x64', 'osx-arm64', 'osx-x64'),
    # Rebuild desktop packages even when their committed inputs did not change.
    [switch]$ForceDesktopBuild,
    # Build and stage the release in artifacts/remote without applying it.
    [switch]$BuildOnly,
    # After deploying, run the live Foundry smoke test as the service user (consumes a little model quota).
    [switch]$FoundrySmokeTest,
    [string]$ExpectFoundryEndpoint = 'https://meldllm.openai.azure.com/openai/v1/'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. ([IO.Path]::Combine($root, 'tools', 'release-common.ps1'))
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
$output = [IO.Path]::Combine($root, 'artifacts', 'remote')
$publish = [IO.Path]::Combine($output, "publish-$Runtime")
$null = [IO.Directory]::CreateDirectory($desktop)
$null = [IO.Directory]::CreateDirectory($output)
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
foreach ($required in @('MeldResearch.RemoteBackend', 'wwwroot/theme.css', 'wwwroot/privacy.html', 'wwwroot/docs.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish output is missing $required." }
}

# 3. Stage the release. apply-release.sh publishes a desktop release only if it differs from the live one.
$stage = [IO.Path]::Combine($output, "release-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))")
Copy-Item -LiteralPath $publish -Destination ([IO.Path]::Combine($stage, 'app')) -Recurse
[IO.File]::WriteAllText([IO.Path]::Combine($stage, 'RUNTIME'), $Runtime)
$null = [IO.Directory]::CreateDirectory([IO.Path]::Combine($stage, 'deploy'))
[IO.File]::WriteAllText([IO.Path]::Combine($stage, 'deploy', 'apply-release.sh'),
    [IO.File]::ReadAllText([IO.Path]::Combine($PSScriptRoot, 'apply-release.sh')).Replace("`r`n", "`n"))
$downloads = [IO.Directory]::CreateDirectory([IO.Path]::Combine($stage, 'downloads')).FullName
foreach ($desktopRuntime in $DesktopRuntimes) {
    Copy-Item -LiteralPath ([IO.Path]::Combine($desktop, "meldresearch-$desktopRuntime.zip")), `
        ([IO.Path]::Combine($desktop, "meldresearch-$desktopRuntime.update.json")) -Destination $downloads
}
if ($BuildOnly) { Write-Host "Staged release (not applied): $stage"; return }

try {
    $deployed = [IO.Path]::Combine($root, 'deployed')
    & bash ([IO.Path]::Combine($stage, 'deploy', 'apply-release.sh')) --release $stage --deploy-root $deployed
    if ($LASTEXITCODE -ne 0) { throw 'Applying the release failed. See the output above.' }
    if ($FoundrySmokeTest) {
        # Same user, configuration and managed identity as the service.
        Write-Host '==> Foundry smoke test (service user, managed identity)'
        $data = [IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'meldresearch-data')
        & sudo systemd-run --wait --pipe --quiet --collect -p "User=$(& id -un)" -p "EnvironmentFile=$data/remote.env" `
            -p Environment=ASPNETCORE_ENVIRONMENT=Production ([IO.Path]::Combine($deployed, 'current', 'MeldResearch.RemoteBackend')) `
            --foundry-smoke-test --expect-endpoint $ExpectFoundryEndpoint
        if ($LASTEXITCODE -ne 0) { throw 'The Foundry smoke test failed.' }
    }
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
