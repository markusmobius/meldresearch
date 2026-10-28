#!/usr/bin/env bash
# Applies a release staged by deploy/deploy.ps1. Runs as you (the user the service runs as); it uses sudo only
# to restart the systemd service. Layout (README: "Where things live on the server"):
#   <repo>/deployed/releases/<stamp>.<id>/   one compiled remote backend per deploy (latest five kept)
#   <repo>/deployed/current                  symlink to the release that systemd runs
#   ~/meldresearch-data/downloads/           desktop releases served to installed clients
#   bash apply-release.sh --release <staged release> --deploy-root <repo>/deployed
set -euo pipefail

SERVICE="meldresearch-remote"
DEPLOY_ROOT=""
DOWNLOADS="${MELDRESEARCH_DOWNLOADS:-$HOME/meldresearch-data/downloads}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/healthz}"
SYSTEMCTL="${MELDRESEARCH_SYSTEMCTL:-sudo systemctl}"   # overridable only for isolated tests
KEEP_RELEASES=5
RELEASE=""

die() { echo "ERROR: $*" >&2; exit 1; }
note() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --release) RELEASE="$2"; shift 2 ;;
        --deploy-root) DEPLOY_ROOT="$2"; shift 2 ;;
        *) die "Unknown argument: $1" ;;
    esac
done

[[ $EUID -ne 0 ]] || die "Run as the user the service runs as, not root."
[[ -n "$RELEASE" && -f "$RELEASE/app/MeldResearch.RemoteBackend" ]] || die "--release must contain app/MeldResearch.RemoteBackend."
[[ -n "$DEPLOY_ROOT" ]] || die "--deploy-root is required (normally <repository>/deployed)."
RELEASE="$(cd "$RELEASE" && pwd)"
case "$(uname -m)" in
    x86_64) MACHINE_RUNTIME="linux-x64" ;;
    aarch64) MACHINE_RUNTIME="linux-arm64" ;;
    *) die "Unsupported CPU architecture: $(uname -m)" ;;
esac
[[ "$(cat "$RELEASE/RUNTIME")" == "$MACHINE_RUNTIME" ]] || die "This release is $(cat "$RELEASE/RUNTIME"); the server needs $MACHINE_RUNTIME."
$SYSTEMCTL cat "$SERVICE" >/dev/null 2>&1 || die "systemd unit $SERVICE is not installed. Complete the one-time server setup first."
command -v curl >/dev/null || die "curl is required for the health check."
mkdir -p "$DEPLOY_ROOT/releases"
DEPLOY_ROOT="$(cd "$DEPLOY_ROOT" && pwd)"

note "Installing the remote backend"
stamp="$(date -u +%Y%m%d%H%M%S)"
target="$(mktemp -d "$DEPLOY_ROOT/releases/$stamp.XXXX")"
chmod 0755 "$target"
cp -r "$RELEASE/app/." "$target/"
chmod 0755 "$target/MeldResearch.RemoteBackend"
[[ -f "$target/createdump" ]] && chmod 0755 "$target/createdump"
previous="$(readlink -f "$DEPLOY_ROOT/current" 2>/dev/null || true)"
ln -sfn "$target" "$DEPLOY_ROOT/current.new"
mv -Tf "$DEPLOY_ROOT/current.new" "$DEPLOY_ROOT/current"

healthy() {
    for _ in $(seq 1 30); do
        curl -fsS --max-time 2 "$HEALTH_URL" >/dev/null 2>&1 && return 0
        sleep 1
    done
    return 1
}

note "Restarting $SERVICE"
$SYSTEMCTL restart "$SERVICE"
if ! healthy; then
    $SYSTEMCTL status "$SERVICE" --no-pager -n 40 >&2 || true
    if [[ -n "$previous" && -d "$previous" && "$previous" != "$target" ]]; then
        echo "Health check failed; rolling back to $previous" >&2
        ln -sfn "$previous" "$DEPLOY_ROOT/current.new"
        mv -Tf "$DEPLOY_ROOT/current.new" "$DEPLOY_ROOT/current"
        $SYSTEMCTL restart "$SERVICE"
    fi
    die "The new release did not become healthy at $HEALTH_URL. No desktop update was published."
fi

# Publish desktop releases only after the backend that serves them is healthy. Each package is replaced
# before its signed manifest, so clients never see a manifest that names a missing package.
shopt -s nullglob
for manifest in "$RELEASE"/downloads/meldresearch-*.update.json; do
    package="${manifest%.update.json}.zip"
    [[ -f "$package" ]] || die "$(basename "$manifest") has no matching package."
    mkdir -p "$DOWNLOADS"
    chmod 0700 "$DOWNLOADS"
    if cmp -s "$manifest" "$DOWNLOADS/$(basename "$manifest")" && cmp -s "$package" "$DOWNLOADS/$(basename "$package")"; then
        note "Desktop release $(basename "$package") is already published"
        continue
    fi
    for file in "$package" "$manifest"; do
        name="$(basename "$file")"
        cp "$file" "$DOWNLOADS/.$name.partial"
        chmod 0600 "$DOWNLOADS/.$name.partial"
        mv -f "$DOWNLOADS/.$name.partial" "$DOWNLOADS/$name"
    done
    note "Published desktop release $(basename "$package")"
done
shopt -u nullglob

ls -1dt "$DEPLOY_ROOT"/releases/* | tail -n +$((KEEP_RELEASES + 1)) | while read -r old; do
    [[ "$old" == "$(readlink -f "$DEPLOY_ROOT/current")" ]] || rm -rf -- "$old"
done
note "Deployed $(basename "$target")"
