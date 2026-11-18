#!/usr/bin/env bash
# Installs the .deb files of a folder with apt, as root in a clean Debian or
# Ubuntu (a container), and checks them: the smoke test through /usr/bin/acrux
# and, on amd64, the desktop app opening a parquet on Xvfb. With
# --expect-refused, apt must turn the command line down instead (the arm64
# build on a glibc older than 2.39).
#
#   installer/deb/test-deb.sh <folder with the .deb files> [--expect-refused]
#
# CI runs it in each container of the "deb" job; locally:
#   podman run --rm -v .:/repo:ro,Z -v <debs>:/debs:ro,Z debian:13 /repo/installer/deb/test-deb.sh /debs
set -uo pipefail

# absolute: apt reads "debs/acrux_….deb" as package "debs" from release
# "acrux_….deb"; only a path starting with / or ./ is a file to it
debs=$(cd "${1:?folder with the .deb files}" && pwd)
here=$(cd "$(dirname "$0")/../.." && pwd)
export DEBIAN_FRONTEND=noninteractive
. /etc/os-release
arch=$(dpkg --print-architecture)

packages=("$debs"/acrux_*_"$arch".deb)
[[ $arch == amd64 ]] && packages+=("$debs"/acrux-desktop_*_amd64.deb)
extra=(sqlite3 tzdata diffutils)
[[ $arch == amd64 ]] && extra+=(xvfb)

apt-get update -qq > /dev/null
if [[ ${2:-} == --expect-refused ]]; then
    # without -qq: it hides the line that says why apt refused
    if apt-get install -y "${packages[@]}" > /tmp/apt.log 2>&1; then
        echo "$PRETTY_NAME $arch: apt installed acrux, which should have been refused" >&2
        exit 1
    fi
    grep -E 'Depends: libc6' /tmp/apt.log || { cat /tmp/apt.log; exit 1; }
    echo "$PRETTY_NAME $arch: refused, as expected"
    exit 0
fi

apt-get install -y -qq "${packages[@]}" "${extra[@]}" > /tmp/apt.log 2>&1 || { cat /tmp/apt.log; exit 1; }
echo "$PRETTY_NAME $arch: $(acrux version), $(dpkg-query -W -f '${Package} ' acrux acrux-desktop 2>/dev/null)"

work=$(mktemp -d)
cp -r "$here/tests" "$work/"
bash "$work/tests/smoke/run.sh" /usr/bin/acrux || exit 1

if [[ $arch == amd64 ]]; then
    Xvfb :99 -screen 0 1280x800x24 > /dev/null 2>&1 &
    sleep 2
    DISPLAY=:99 timeout 10 acrux-desktop "$work/tests/smoke/fixtures/tipos.parquet" > "$work/app.log" 2>&1
    status=$?
    kill %1 2> /dev/null
    # 124: still open when the timeout ended it, which is what we want
    if [[ $status != 124 || -s $work/app.log ]]; then
        echo "the desktop app exited with $status:" >&2
        cat "$work/app.log" >&2
        exit 1
    fi
    echo "the desktop app opened a parquet and stayed open"
fi
