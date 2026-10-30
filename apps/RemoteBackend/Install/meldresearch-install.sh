#!/bin/bash
# Meld Research -- installer for macOS (per user; no administrator rights needed).
#   curl -fsSL {{REMOTEURL}}/api/install/macos | bash
# Downloads the signed desktop release published by {{REMOTEURL}}, checks its length and SHA-256 against the
# release manifest, and runs the package's own installer. That installer adds the release to
# ~/Library/Application Support/MeldResearch/App, starts it at every login (LaunchAgent), and opens the local app.
# Running this again repairs or updates an existing installation; research data is kept.
set -euo pipefail

REMOTE_URL="{{REMOTEURL}}"
TOTAL_STEPS=3

step() {
    echo ""
    echo "=========================================================================="
    echo "  STEP $1 / $TOTAL_STEPS  --  $2"
    echo "=========================================================================="
}
fail() {
    echo "" >&2
    echo "  [FAIL]  $1" >&2
    echo "          Installation did not complete. Please report the step number above." >&2
    exit 1
}

# Everything runs inside main so that "curl | bash" executes only a completely downloaded script.
main() {
    step 1 "Check this computer"
    [ "$(uname -s)" = Darwin ] || fail "This installer is for macOS. On Windows, use the PowerShell command."
    local runtime=osx-x64
    if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then runtime=osx-arm64; fi
    echo "  macOS ($runtime). The package is self-contained: no .NET installation is needed."

    step 2 "Download and verify the signed release"
    work=$(mktemp -d "${TMPDIR:-/tmp}/meldresearch-install.XXXXXX")
    trap 'rm -rf -- "$work"' EXIT
    /usr/bin/curl -fsSL "$REMOTE_URL/api/updates/$runtime" -o "$work/envelope.json" ||
        fail "No desktop release is published for $runtime."
    /usr/bin/plutil -extract manifest raw -o - "$work/envelope.json" | /usr/bin/base64 -D > "$work/manifest.json" ||
        fail "The server returned an invalid release manifest."
    local version length sha256
    version=$(/usr/bin/plutil -extract version raw -o - "$work/manifest.json")
    length=$(/usr/bin/plutil -extract length raw -o - "$work/manifest.json")
    sha256=$(/usr/bin/plutil -extract sha256 raw -o - "$work/manifest.json")
    [ "$(/usr/bin/plutil -extract runtime raw -o - "$work/manifest.json")" = "$runtime" ] ||
        fail "The server returned a manifest for another platform."
    case "$version$sha256" in *[!0-9a-f]*) fail "The server returned an invalid release manifest.";; esac
    [ "${#version}" -eq 64 ] && [ "${#sha256}" -eq 64 ] || fail "The server returned an invalid release manifest."
    echo "  Release ${version:0:12}"
    /usr/bin/curl -fsSL "$REMOTE_URL/api/updates/$runtime/package/$version" -o "$work/package.zip" ||
        fail "The desktop package could not be downloaded."
    [ "$(/usr/bin/stat -f %z "$work/package.zip")" = "$length" ] &&
        [ "$(/usr/bin/shasum -a 256 "$work/package.zip" | cut -d ' ' -f 1)" = "$sha256" ] ||
        fail "The downloaded package does not match the release manifest. Nothing was installed."
    echo "  Downloaded; length and SHA-256 match the release manifest."

    step 3 "Install and start at login"
    /usr/bin/ditto -x -k "$work/package.zip" "$work/package" || fail "The package could not be extracted."
    /bin/sh "$work/package/Install-Meld.command" || fail "The package installer reported an error (see above)."
}

main "$@"
