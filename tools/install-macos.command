#!/bin/sh
# Installs this release next to any earlier ones under ~/Library/Application Support/MeldResearch/App, points the
# stable launcher at it, and registers the per-user native browser bridge. Later releases install automatically.
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
if [ -e "$target" ]; then
  echo "This version is already installed. Existing files were not replaced." >&2
  exit 1
fi
mkdir -p "$target"
for name in local browser-host frontend browser installation.json Start-Meld.command THIRD-PARTY-NOTICES.md; do
  cp -R "$source_dir/$name" "$target/"
done
chmod 700 "$target/local/MeldResearch.LocalBackend" "$target/browser-host/MeldResearch.BrowserHost" "$target/Start-Meld.command"
if [ "${MELDRESEARCH_Updates__RegisterNativeHost:-true}" != false ]; then
  "$target/browser-host/MeldResearch.BrowserHost" --register
  echo "Registered the per-user browser bridge. No browser policy was changed."
fi
previous=""
if [ -f "$root/current.json" ]; then previous=$(/usr/bin/plutil -extract current raw -o - "$root/current.json" 2>/dev/null || true); fi
printf '{"current":"%s","previous":"%s","rejected":[]}\n' "$folder" "$previous" > "$root/current.json.partial"
mv -f "$root/current.json.partial" "$root/current.json"
cp "$target/Start-Meld.command" "$root/Start-Meld.command.partial"
chmod 700 "$root/Start-Meld.command.partial"
mv -f "$root/Start-Meld.command.partial" "$root/Start-Meld.command"
echo "This unsigned test distribution still requires macOS and browser approval."
echo "Start Meld Research later with: $root/Start-Meld.command"
exec "$root/Start-Meld.command"
