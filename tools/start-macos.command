#!/bin/sh
# Stable Meld Research launcher. It lives in the application root next to current.json and starts the version
# that file names. Exit code 75 means the application installed a verified update and repointed current.json,
# so the launcher starts the new version. A version that fails to start is rolled back to the previous one.
#   --background  started by the LaunchAgent at login: no browser, quiet if Meld Research is already running.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
pointer="$root/current.json"
port=6001
opened=false
background=false
if [ "${1:-}" = --background ]; then background=true; fi
pid=
if [ ! -f "$pointer" ]; then
  echo "Meld Research is not installed here. Run Install-Meld.command from the downloaded package." >&2
  exit 1
fi
if [ "$background" = true ] && /usr/bin/curl --silent --fail --max-time 2 "http://127.0.0.1:$port/api/status" >/dev/null 2>&1; then
  exit 0
fi
trap 'if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; fi' EXIT
trap 'exit 130' INT TERM

field() { /usr/bin/plutil -extract "$1" raw -o - "$2" 2>/dev/null || true; }
is_folder() { [ "${#1}" -eq 12 ] && case "$1" in *[!0-9a-f]*) false;; *) true;; esac; }

while :; do
  if /usr/sbin/lsof -n -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "Port $port is occupied. No process was stopped." >&2
    exit 1
  fi
  current=$(field current "$pointer")
  previous=$(field previous "$pointer")
  if ! is_folder "$current"; then echo "The installation pointer is invalid." >&2; exit 1; fi
  directory="$root/$current"
  (cd "$directory/local" && exec ./MeldResearch.LocalBackend --Port "$port" --FrontendPath "$directory/frontend" \
    --ConnectorDirectory "$directory/browser/overleaf" --InstallRoot "$root") &
  pid=$!
  attempt=0
  ready=false
  while kill -0 "$pid" 2>/dev/null && [ "$attempt" -lt 120 ]; do
    if /usr/bin/curl --silent --fail --max-time 2 "http://127.0.0.1:$port/api/status" >/dev/null; then ready=true; break; fi
    attempt=$((attempt + 1))
    sleep 0.25
  done
  if [ "$ready" != true ]; then
    kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; pid=
    if [ -n "$previous" ] && is_folder "$previous" && [ -d "$root/$previous" ]; then
      echo "Meld Research $current did not start. Returning to the previous version $previous." >&2
      failed=$(field Version "$directory/installation.json")
      if [ "${MELDRESEARCH_Updates__RegisterNativeHost:-true}" != false ]; then
        "$root/$previous/browser-host/MeldResearch.BrowserHost" --register
      fi
      printf '{"current":"%s","previous":"","rejected":["%s"]}\n' "$previous" "$failed" > "$pointer.partial"
      mv -f "$pointer.partial" "$pointer"
      cp "$root/$previous/Start-Meld.command" "$root/Start-Meld.command.partial"
      chmod 700 "$root/Start-Meld.command.partial"
      mv -f "$root/Start-Meld.command.partial" "$root/Start-Meld.command"
      continue
    fi
    echo "Meld did not become ready." >&2
    exit 1
  fi
  if [ "$opened" = false ]; then
    opened=true
    if [ "$background" != true ] && [ "${MELDRESEARCH_NO_BROWSER:-false}" != true ]; then
      published=$(field StorePublished "$directory/installation.json")
      if [ "$published" = true ]; then
        id=$(field ExtensionId "$directory/installation.json")
        case "$id" in *[!a-p]*|"") echo "Invalid connector identity." >&2; exit 1;; esac
        /usr/bin/open "https://chromewebstore.google.com/detail/$id"
      fi
      /usr/bin/open "http://127.0.0.1:$port"
    fi
    echo "Link your existing Meld account and approve selected-project processing."
    if [ "$background" != true ]; then
      echo "Keep this terminal open. Ctrl+C stops this owned instance. Updates install automatically."
    fi
  fi
  status=0
  wait "$pid" || status=$?
  pid=
  if [ "$status" -eq 75 ]; then
    echo "Restarting Meld Research into the installed update..."
    continue
  fi
  exit "$status"
done
