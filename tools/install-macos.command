#!/bin/sh
# Installs this release next to any earlier ones under ~/Library/Application Support/MeldResearch/App, points the
# stable launcher at it, registers the per-user native browser bridge, and starts Meld Research in the background at
# every login (a per-user LaunchAgent; no administrator rights). Later releases install automatically.
# Running it again for an installed release repairs the launcher and LaunchAgent without copying files.
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
version=$(/usr/bin/plutil -extract Version raw -o - "$source_dir/installation.json")
runtime=$(/usr/bin/plutil -extract Runtime raw -o - "$source_dir/installation.json")
case "$version" in *[!0-9a-f]*|"") echo "Invalid package version." >&2; exit 1;; esac
if [ "${#version}" -ne 64 ]; then echo "Invalid package version." >&2; exit 1; fi
case "$runtime" in osx-arm64|osx-x64) ;; *) echo "This package is not for macOS." >&2; exit 1;; esac
root="$HOME/Library/Application Support/MeldResearch/App"
folder=$(printf '%s' "$version" | cut -c1-12)
target="$root/$folder"
port=6001
label=org.meldresearch.desktop
agent="$HOME/Library/LaunchAgents/$label.plist"
logs="$HOME/Library/Logs/MeldResearch"
# Isolated tests set MELDRESEARCH_Install__Autostart=false: no LaunchAgent, and the launcher runs in this terminal.
autostart=${MELDRESEARCH_Install__Autostart:-true}
port_busy() { /usr/sbin/lsof -n -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; }

echo "  Stopping a running Meld Research, if any..."
if [ "$autostart" != false ]; then /bin/launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true; fi
# Only this installation's own launcher and application processes are stopped.
/usr/bin/pkill -f "$root/Start-Meld.command" 2>/dev/null || true
/usr/bin/pkill -f "MeldResearch.LocalBackend .*--InstallRoot $root" 2>/dev/null || true
attempt=0
while port_busy && [ "$attempt" -lt 40 ]; do sleep 0.25; attempt=$((attempt + 1)); done
if port_busy; then
  echo "Port $port is used by another application. Close it and run the installer again; nothing was stopped." >&2
  exit 1
fi

if [ -e "$target" ]; then
  echo "  Release $folder is already installed; reusing its files."
else
  echo "  Installing release $folder to $target"
  rm -rf "$target.partial"
  mkdir -p "$target.partial"
  for name in local browser-host frontend browser installation.json Start-Meld.command THIRD-PARTY-NOTICES.md; do
    cp -R "$source_dir/$name" "$target.partial/"
  done
  chmod 700 "$target.partial/local/MeldResearch.LocalBackend" "$target.partial/browser-host/MeldResearch.BrowserHost" \
    "$target.partial/Start-Meld.command"
  mv "$target.partial" "$target"
fi
if [ "${MELDRESEARCH_Updates__RegisterNativeHost:-true}" != false ]; then
  "$target/browser-host/MeldResearch.BrowserHost" --register
  echo "  Registered the per-user browser bridge. No browser policy was changed."
fi
previous=""
if [ -f "$root/current.json" ]; then
  old=$(/usr/bin/plutil -extract current raw -o - "$root/current.json" 2>/dev/null || true)
  if [ "$old" = "$folder" ]; then
    previous=$(/usr/bin/plutil -extract previous raw -o - "$root/current.json" 2>/dev/null || true)
  else
    previous=$old
  fi
fi
printf '{"current":"%s","previous":"%s","rejected":[]}\n' "$folder" "$previous" > "$root/current.json.partial"
mv -f "$root/current.json.partial" "$root/current.json"
cp "$target/Start-Meld.command" "$root/Start-Meld.command.partial"
chmod 700 "$root/Start-Meld.command.partial"
mv -f "$root/Start-Meld.command.partial" "$root/Start-Meld.command"

if [ "$autostart" = false ]; then
  echo "This unsigned test distribution still requires macOS and browser approval."
  exec "$root/Start-Meld.command"
fi

echo "  Registering the login item (LaunchAgent $label)..."
mkdir -p "$HOME/Library/LaunchAgents" "$logs"
xml() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }
cat > "$agent.partial" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/sh</string>
    <string>$(xml "$root/Start-Meld.command")</string>
    <string>--background</string>
  </array>
  <key>WorkingDirectory</key><string>$(xml "$root")</string>
  <key>RunAtLoad</key><true/>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
  <key>StandardOutPath</key><string>$(xml "$logs/launcher.log")</string>
  <key>StandardErrorPath</key><string>$(xml "$logs/launcher.log")</string>
</dict>
</plist>
EOF
/usr/bin/plutil -lint -s "$agent.partial"
chmod 644 "$agent.partial"
mv -f "$agent.partial" "$agent"
/bin/launchctl bootstrap "gui/$(id -u)" "$agent"

echo "  Waiting for Meld Research to start..."
attempt=0
until /usr/bin/curl --silent --fail --max-time 2 "http://127.0.0.1:$port/api/version" 2>/dev/null | grep -q "\"$version\""; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 120 ]; then
    tail -n 20 "$logs/launcher.log" 2>/dev/null | sed 's/^/    /' >&2 || true
    echo "Meld Research did not start. The launcher log is $logs/launcher.log." >&2
    exit 1
  fi
  sleep 0.5
done
if [ "${MELDRESEARCH_NO_BROWSER:-false}" != true ]; then
  if [ "$(/usr/bin/plutil -extract StorePublished raw -o - "$target/installation.json" 2>/dev/null || true)" = true ]; then
    id=$(/usr/bin/plutil -extract ExtensionId raw -o - "$target/installation.json")
    case "$id" in *[!a-p]*|"") echo "Invalid connector identity." >&2; exit 1;; esac
    /usr/bin/open "https://chromewebstore.google.com/detail/$id"
  fi
  /usr/bin/open "http://127.0.0.1:$port"
fi
echo ""
echo "=========================================================================="
echo "  [OK]  Meld Research is running at http://127.0.0.1:$port"
echo "  [OK]  It starts in the background when you log in and updates itself."
echo "=========================================================================="
echo "Link your Meld account under Account, approve the browser connector, then select projects and consent to processing."
echo "This unsigned test distribution may still require macOS and browser approval."
