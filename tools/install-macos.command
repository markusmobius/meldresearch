#!/bin/sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
version=$(/usr/bin/plutil -extract Version raw -o - "$source_dir/installation.json")
case "$version" in *[!0-9.]*|"") echo "Invalid package version." >&2; exit 1;; esac
target="$HOME/Library/Application Support/MeldResearch/App/$version"
if [ -e "$target" ]; then
  echo "This version is already installed. Existing files were not replaced." >&2
  exit 1
fi
mkdir -p "$target"
for name in local browser-host frontend browser installation.json Start-Meld.command THIRD-PARTY-NOTICES.md; do
  cp -R "$source_dir/$name" "$target/"
done
chmod 700 "$target/local/MeldResearch.LocalBackend" "$target/browser-host/MeldResearch.BrowserHost" "$target/Start-Meld.command"
"$target/browser-host/MeldResearch.BrowserHost" --register
echo "Registered the per-user browser bridge. No browser policy was changed."
echo "This unsigned test distribution still requires macOS and browser approval."
exec "$target/Start-Meld.command"
