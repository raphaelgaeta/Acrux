#!/usr/bin/env bash
# Installs Acrux for this user, without root (the Linux side of installer/acrux.iss):
# - the app in ~/.local/share/acrux;
# - a menu entry and "Open with" for parquet, CSV, xlsx and SQLite;
# - the parquet type, which most desktops don't know yet (IANA, 2024);
# - parquet opens in Acrux on a double click, unless another app already does.
#   CSV, xlsx and SQLite keep their app, with Acrux in the list, as on Windows.
#   An app in ~/.local/share outranks the system's, so the apps that opened these
#   types before are written down as their defaults: otherwise Acrux would take
#   every type it lists from whoever had no default written.
# uninstall.sh, in the same folder, takes all of it back.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
app=$data/acrux

if [[ ! -x $here/Acrux ]]; then
    echo "install.sh: Acrux not found next to this script; run it from the extracted folder." >&2
    exit 1
fi

# the formats installer/acrux.iss registers; installer/rpm/acrux.spec writes
# the same list into its own acrux.desktop
types=(application/vnd.apache.parquet text/csv
    application/vnd.openxmlformats-officedocument.spreadsheetml.sheet application/vnd.sqlite3)
declare -A before   # who opens each type now, before Acrux is in the list
xdg=$(command -v xdg-mime || true)
if [[ -n $xdg ]]; then
    for type in "${types[@]}"; do before[$type]=$(xdg-mime query default "$type" 2> /dev/null || true); done
fi

# a fresh copy, so an older version's files don't linger; skipped when this
# already is the installed copy
if [[ $here != "$app" ]]; then
    rm -rf "$app.new"
    mkdir -p "$app.new"
    cp -a "$here/." "$app.new/"
    rm -rf "$app"
    mv "$app.new" "$app"
fi

mkdir -p "$data/applications" "$data/mime/packages" "$data/icons/hicolor/scalable/apps"
cp "$app/share/acrux-parquet.xml" "$data/mime/packages/acrux-parquet.xml"
cp "$app/share/acrux.svg" "$data/icons/hicolor/scalable/apps/acrux.svg"
cat > "$data/applications/acrux.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Acrux
GenericName=Data viewer
Comment=Open parquet, CSV, Excel and SQLite files of any size
Exec="$app/Acrux" %f
Icon=acrux
Terminal=false
Categories=Office;Viewer;
MimeType=$(IFS=';'; echo "${types[*]}");
DESKTOP

# the desktop's caches; without them the type and the entry show up only after a re-login
if command -v update-mime-database > /dev/null; then update-mime-database "$data/mime"; fi
if command -v update-desktop-database > /dev/null; then update-desktop-database "$data/applications"; fi
owner=""
if [[ -n $xdg ]]; then
    for type in "${types[@]}"; do
        app_before=${before[$type]:-}
        if [[ -n $app_before && $app_before != acrux.desktop ]]; then
            xdg-mime default "$app_before" "$type"   # it keeps the type (LibreOffice Calc claims parquet since 26.2)
        elif [[ $type == application/vnd.apache.parquet ]]; then
            xdg-mime default acrux.desktop "$type"
        fi
    done
    owner=${before[application/vnd.apache.parquet]:-acrux.desktop}
fi

echo "Acrux is installed in $app."
echo "Open it from the menu, with \"Open with\" on a data file, or run: \"$app/Acrux\" [file]"
if [[ -n $owner && $owner != acrux.desktop ]]; then
    echo "Parquet files open in $owner. To open them in Acrux with a double click:"
    echo "  xdg-mime default acrux.desktop application/vnd.apache.parquet"
fi
echo "To remove it: \"$app/uninstall.sh\""
