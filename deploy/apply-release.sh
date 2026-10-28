#!/usr/bin/env bash
# Applies a release staged by deploy/deploy.ps1 to a server that was set up once by hand
# (README: "Server setup"). It never installs packages, nginx, certificates, systemd units, users or secrets.
#   sudo bash <release>/deploy/apply-release.sh --release <release>
set -euo pipefail

SERVICE="meldresearch-remote"
APP_USER="meldresearch"
# Overridable only for isolated tests of this script.
INSTALL_ROOT="${MELDRESEARCH_INSTALL_ROOT:-/opt/meldresearch}"
DOWNLOADS="${MELDRESEARCH_DOWNLOADS:-/var/lib/meldresearch/downloads}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/healthz}"
KEEP_RELEASES=5
RELEASE=""

die() { echo "ERROR: $*" >&2; exit 1; }
note() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --release) RELEASE="$2"; shift 2 ;;
        *) die "Unknown argument: $1" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "Run as root (sudo)."
[[ -n "$RELEASE" && -f "$RELEASE/app/MeldResearch.RemoteBackend" ]] || die "--release must contain app/MeldResearch.RemoteBackend."
RELEASE="$(cd "$RELEASE" && pwd)"
case "$(uname -m)" in
    x86_64) MACHINE_RUNTIME="linux-x64" ;;
    aarch64) MACHINE_RUNTIME="linux-arm64" ;;
    *) die "Unsupported CPU architecture: $(uname -m)" ;;
esac
[[ "$(cat "$RELEASE/RUNTIME")" == "$MACHINE_RUNTIME" ]] || die "This release is $(cat "$RELEASE/RUNTIME"); the server needs $MACHINE_RUNTIME."

# The one-time setup is yours; verify it instead of performing it.
id -u "$APP_USER" >/dev/null 2>&1 || die "User $APP_USER does not exist. Complete the one-time server setup first."
systemctl cat "$SERVICE" >/dev/null 2>&1 || die "systemd unit $SERVICE is not installed. Complete the one-time server setup first."
command -v curl >/dev/null || die "curl is required for the health check."
install -d -m 0755 -o root -g root "$INSTALL_ROOT" "$INSTALL_ROOT/releases"

note "Installing the remote backend"
stamp="$(date -u +%Y%m%d%H%M%S)"
target="$(mktemp -d "$INSTALL_ROOT/releases/$stamp.XXXX")"
cp -r "$RELEASE/app/." "$target/"
chown -R root:root "$target"
find "$target" -type d -exec chmod 0755 {} +
find "$target" -type f -exec chmod 0644 {} +
chmod 0755 "$target/MeldResearch.RemoteBackend"
[[ -f "$target/createdump" ]] && chmod 0755 "$target/createdump"
previous="$(readlink -f "$INSTALL_ROOT/current" 2>/dev/null || true)"
ln -sfn "$target" "$INSTALL_ROOT/current.new"
mv -Tf "$INSTALL_ROOT/current.new" "$INSTALL_ROOT/current"

healthy() {
    for _ in $(seq 1 30); do
        curl -fsS --max-time 2 "$HEALTH_URL" >/dev/null 2>&1 && return 0
        sleep 1
    done
    return 1
}

note "Restarting $SERVICE"
systemctl restart "$SERVICE"
if ! healthy; then
    journalctl -u "$SERVICE" -n 60 --no-pager >&2 || true
    if [[ -n "$previous" && -d "$previous" && "$previous" != "$target" ]]; then
        echo "Health check failed; rolling back to $previous" >&2
        ln -sfn "$previous" "$INSTALL_ROOT/current.new"
        mv -Tf "$INSTALL_ROOT/current.new" "$INSTALL_ROOT/current"
        systemctl restart "$SERVICE"
    fi
    die "The new release did not become healthy at $HEALTH_URL. No desktop update was published."
fi

# Publish desktop releases only after the backend that serves them is healthy. Each package is replaced
# before its signed manifest, so clients never see a manifest that names a missing package.
shopt -s nullglob
for manifest in "$RELEASE"/downloads/meldresearch-*.update.json; do
    package="${manifest%.update.json}.zip"
    [[ -f "$package" ]] || die "$(basename "$manifest") has no matching package."
    install -d -m 0700 -o "$APP_USER" -g "$APP_USER" "$DOWNLOADS"
    if cmp -s "$manifest" "$DOWNLOADS/$(basename "$manifest")" && cmp -s "$package" "$DOWNLOADS/$(basename "$package")"; then
        note "Desktop release $(basename "$package") is already published"
        continue
    fi
    for file in "$package" "$manifest"; do
        name="$(basename "$file")"
        install -m 0600 -o "$APP_USER" -g "$APP_USER" "$file" "$DOWNLOADS/.$name.partial"
        mv -f "$DOWNLOADS/.$name.partial" "$DOWNLOADS/$name"
    done
    note "Published desktop release $(basename "$package")"
done
shopt -u nullglob

ls -1dt "$INSTALL_ROOT"/releases/* | tail -n +$((KEEP_RELEASES + 1)) | while read -r old; do
    [[ "$old" == "$(readlink -f "$INSTALL_ROOT/current")" ]] || rm -rf -- "$old"
done
note "Deployed $(basename "$target")"
