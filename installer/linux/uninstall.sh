#!/usr/bin/env bash
# Removes what install.sh put in place. A parquet default that pointed to Acrux
# stays in mimeapps.list: without the menu entry, the desktop ignores it, as
# Windows ignores a default without its ProgID.
set -euo pipefail

data=${XDG_DATA_HOME:-$HOME/.local/share}
rm -f "$data/applications/acrux.desktop" "$data/mime/packages/acrux-parquet.xml" \
    "$data/icons/hicolor/scalable/apps/acrux.svg"
if command -v update-mime-database > /dev/null; then update-mime-database "$data/mime"; fi
if command -v update-desktop-database > /dev/null; then update-desktop-database "$data/applications"; fi
# the parquet copies of CSV, xlsx and SQLite files, unless an Acrux is still open
if ! pgrep -x Acrux > /dev/null; then rm -rf "${TMPDIR:-/tmp}/Acrux"; fi
rm -rf "$data/acrux"
echo "Acrux is removed."
