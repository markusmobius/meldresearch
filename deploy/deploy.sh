#!/usr/bin/env bash
# Bash entry point for server deploys. Ensures the runtime prerequisites (curl, sqlite3, the ICU library .NET
# needs, and PowerShell 7) on Ubuntu/Debian, checks the build tools, then runs deploy/deploy.ps1 with the same
# arguments. Run as your deploy user (it uses sudo only to install packages and to apply the release):
#   bash deploy/deploy.sh [--yes] [-FoundrySmokeTest] [-BuildOnly] [-DesktopRuntimes win-x64,osx-arm64] ...
#   bash deploy/deploy.sh --prerequisites [--yes]    # install/verify prerequisites only, then stop
# --yes installs missing prerequisites without asking (sudo; PowerShell adds Microsoft's apt repository).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
assume_yes=false
prerequisites_only=false
args=()
for argument in "$@"; do
    case "$argument" in
        --yes|-y) assume_yes=true ;;
        --prerequisites) prerequisites_only=true ;;
        *) args+=("$argument") ;;
    esac
done

die() { echo "ERROR: $*" >&2; exit 1; }

confirm() {
    $assume_yes && return 0
    [[ -t 0 ]] || die "$1 Re-run with --yes to allow it non-interactively."
    read -r -p "$1 [y/N] " answer
    [[ "$answer" =~ ^[Yy]$ ]]
}

# Prints ubuntu or debian for apt-based systems, or nothing.
apt_distribution() {
    [[ -r /etc/os-release ]] || return 0
    (
        . /etc/os-release
        if [[ "$ID" == ubuntu || " ${ID_LIKE:-} " == *" ubuntu "* ]]; then echo ubuntu
        elif [[ "$ID" == debian || " ${ID_LIKE:-} " == *" debian "* ]]; then echo debian
        fi
    )
}

ensure_packages() {
    local needed=() icu=false
    command -v curl >/dev/null || needed+=(curl)
    command -v sqlite3 >/dev/null || needed+=(sqlite3)
    ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so' || icu=true
    if ! $icu && ((${#needed[@]} == 0)); then return 0; fi
    local description="${needed[*]:-}"
    $icu && description="${description:+$description }ICU library"
    [[ -n "$(apt_distribution)" ]] || die "Missing prerequisites ($description). Install them with your package manager."
    confirm "Missing prerequisites: $description. Install them now with sudo apt-get?" || die "Nothing was installed."
    sudo apt-get update
    if $icu; then
        # The runtime library is versioned (libicu74, libicu78, ...); libicu-dev depends on the current one.
        local package
        package="$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1 || true)"
        needed+=("${package:-libicu-dev}")
    fi
    sudo apt-get install -y --no-install-recommends "${needed[@]}"
}

install_powershell() {
    local distribution version name work
    distribution="$(apt_distribution)"
    [[ -n "$distribution" ]] ||
        die "PowerShell 7 is required and automatic installation supports Ubuntu/Debian only. See https://learn.microsoft.com/powershell/scripting/install/installing-powershell"
    confirm "PowerShell 7 (pwsh) is required but not installed. Install it now with sudo from Microsoft's $distribution repository?" ||
        die "PowerShell 7 is required. Nothing was installed."
    version="$(. /etc/os-release && echo "$VERSION_ID")"
    name="$(. /etc/os-release && echo "$PRETTY_NAME")"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' RETURN
    sudo apt-get install -y --no-install-recommends wget ca-certificates
    if wget -q -O "$work/packages-microsoft-prod.deb" "https://packages.microsoft.com/config/$distribution/$version/packages-microsoft-prod.deb"; then
        sudo dpkg -i "$work/packages-microsoft-prod.deb"
        sudo apt-get update
        sudo apt-get install -y powershell
    elif command -v snap >/dev/null; then
        echo "Microsoft has no repository for $name yet; installing the PowerShell snap instead."
        sudo snap install powershell --classic
    else
        die "No Microsoft repository for $name and snap is unavailable. Install the .deb from https://github.com/PowerShell/PowerShell/releases"
    fi
    hash -r
    command -v pwsh >/dev/null || die "PowerShell was installed but pwsh is not on PATH. Open a new shell and retry."
}

ensure_packages
command -v pwsh >/dev/null || install_powershell

missing=()
command -v git >/dev/null || missing+=("git")
command -v dotnet >/dev/null || missing+=(".NET 10 SDK (dotnet-sdk-10.0)")
command -v npm >/dev/null || missing+=("Node 24 with npm")
if ((${#missing[@]})); then
    list="$(printf '%s; ' "${missing[@]}")"
    echo "ERROR: missing build tools: ${list%; }" >&2
    echo "See README: \"Server setup\". Nothing was built." >&2
    exit 1
fi
if $prerequisites_only; then
    echo "Prerequisites are installed: curl, sqlite3, ICU, PowerShell, git, .NET $(dotnet --version), npm $(npm --version)."
    exit 0
fi

exec pwsh -NoProfile -File "$root/deploy/deploy.ps1" "${args[@]}"
