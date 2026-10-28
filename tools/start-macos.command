#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if /usr/sbin/lsof -n -iTCP:6001 -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port 6001 is occupied. No process was stopped." >&2
  exit 1
fi
cd "$root/local"
./MeldResearch.LocalBackend --Port 6001 --FrontendPath "$root/frontend" --ConnectorDirectory "$root/browser/overleaf" &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT
trap 'exit 130' INT TERM
attempt=0
while ! /usr/bin/curl --silent --fail --max-time 2 http://127.0.0.1:6001/api/status >/dev/null; do
  if ! kill -0 "$pid" 2>/dev/null || [ "$attempt" -ge 60 ]; then
    echo "Meld did not become ready." >&2
    exit 1
  fi
  attempt=$((attempt + 1))
  sleep 0.25
done
published=$(/usr/bin/plutil -extract StorePublished raw -o - "$root/installation.json")
if [ "$published" = true ]; then
  id=$(/usr/bin/plutil -extract ExtensionId raw -o - "$root/installation.json")
  case "$id" in *[!a-p]*|"") echo "Invalid connector identity." >&2; exit 1;; esac
  /usr/bin/open "https://chromewebstore.google.com/detail/$id"
fi
/usr/bin/open http://127.0.0.1:6001
echo "Link your existing Meld account and approve selected-project processing."
echo "Keep this terminal open. Ctrl+C stops this owned instance."
wait "$pid"
