#!/usr/bin/env bash
# Builds a .deb for Debian and Ubuntu from a release folder, the same files the
# .tar.gz carries (the RPM in installer/rpm/ does the same for Fedora):
#
#   installer/deb/build-deb.sh acrux         out/acrux-cli <version> <amd64|arm64> <outdir>
#   installer/deb/build-deb.sh acrux-desktop out/acrux     <version> amd64         <outdir>
#
# acrux is the command line, acrux-desktop the desktop app (amd64 only, like
# the release). Both stay self-contained, with their own .NET runtime. The
# input folder isn't changed. Needs dpkg-deb >= 1.19 (--root-owner-group).
set -euo pipefail

package=${1:?package: acrux or acrux-desktop}
folder=${2:?the release folder (out/acrux-cli or out/acrux)}
version=${3:?version}
arch=${4:?amd64 or arm64}
outdir=${5:?output folder}

# a CI version (0.0.0-ci.42) would read as upstream 0.0.0, revision ci.42;
# with "~" it sorts before 0.0.0, as a pre-release should
debversion=${version//-/\~}

# .NET looks ICU up by its versioned name, which changes with every Debian and
# Ubuntu release (70: Ubuntu 22.04, 72: Debian 12, 74: Ubuntu 24.04, 76:
# Debian 13): any one of them will do. A newer system with a newer ICU needs
# its name added here.
icu=$(printf 'libicu%s | ' 80 79 78 77 76 75 74 73 72 71 70)
icu=${icu% | }
case $arch in
    # Polars' native library: glibc 2.28 on x64, 2.39 on arm64 (built without
    # the manylinux container); apt then refuses an older system up front,
    # instead of Polars failing at the first query
    amd64) libc="libc6 (>= 2.28)";;
    arm64) libc="libc6 (>= 2.39)";;
    *) echo "build-deb.sh: unknown architecture $arch" >&2; exit 2;;
esac
depends="$libc, libgcc-s1, libstdc++6, $icu"

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
root=$stage/$package
doc=$root/usr/share/doc/$package
mkdir -p "$root/DEBIAN" "$root/usr/bin" "$doc"

case $package in
    acrux)
        lib=usr/lib/acrux-cli
        exe=Acrux.Cli
        link=acrux
        section=utils
        summary="read large parquet, CSV, Excel and SQLite files from the command line"
        about="This is the command line: schema, columns, the first rows, distinct
 values, filters and time windows."
        ;;
    acrux-desktop)
        [[ $arch == amd64 ]] || { echo "build-deb.sh: the desktop app is amd64 only" >&2; exit 2; }
        lib=usr/lib/acrux
        exe=Acrux
        link=acrux-desktop
        section=misc
        summary="desktop viewer for large parquet, CSV, Excel and SQLite files"
        about="This is the desktop app: a grid that opens a file of any width at once,
 with 700 columns or millions of rows, and reads only the columns on the
 screen. It filters by header like Excel, follows the system's light, dark
 and high-contrast themes, and has a C# terminal for Polars queries."
        # loaded at run time, so nothing in the binaries says so
        depends="$depends, libfontconfig1, libx11-6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2, libice6, libsm6"
        ;;
    *) echo "build-deb.sh: unknown package $package" >&2; exit 2;;
esac

mkdir -p "$root/$lib"
cp -a "$folder/." "$root/$lib/"
ln -s "../${lib#usr/}/$exe" "$root/usr/bin/$link"
# the license files go to /usr/share/doc, which Debian requires
for f in LICENSE NOTICE-logo.md THIRD-PARTY-NOTICES.txt THIRD-PARTY-NOTICES-polars.html; do
    [[ -e $root/$lib/$f ]] || continue
    [[ $f == LICENSE ]] || cp "$root/$lib/$f" "$doc/"
    rm "$root/$lib/$f"
done
rm -f "$root/$lib/acrux"   # the tarball's own link: /usr/bin has it now

{
    echo "Acrux, $(grep -m1 '^Copyright' "$folder/LICENSE")"
    echo "https://github.com/raphaelgaeta/Acrux"
    echo
    echo "Acrux is under the MIT license, below."
    if [[ $package == acrux-desktop ]]; then
        echo "Its logo and icon are under CC BY-ND 4.0: NOTICE-logo.md, in this folder."
    fi
    echo "It includes third-party software, each under its own license:"
    echo "THIRD-PARTY-NOTICES.txt and THIRD-PARTY-NOTICES-polars.html, in this folder."
    echo
    cat "$folder/LICENSE"
} > "$doc/copyright"

if [[ $package == acrux-desktop ]]; then
    share=$root/$lib/share
    install -Dm 0644 "$share/acrux.svg" "$root/usr/share/icons/hicolor/scalable/apps/acrux.svg"
    install -Dm 0644 "$share/acrux-parquet.xml" "$root/usr/share/mime/packages/acrux-parquet.xml"
    rm -r "$share" "$root/$lib/install.sh" "$root/$lib/uninstall.sh"
    # the same entry and file name as installer/linux/install.sh and the RPM,
    # so a per-user install in ~/.local takes its place instead of showing
    # twice; the MIME types follow the extensions of installer/acrux.iss
    mkdir -p "$root/usr/share/applications"
    cat > "$root/usr/share/applications/acrux.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Acrux
GenericName=Data viewer
Comment=Open parquet, CSV, Excel and SQLite files of any size
Exec=acrux-desktop %f
Icon=acrux
Terminal=false
Categories=Office;Viewer;
MimeType=application/vnd.apache.parquet;text/csv;application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;application/vnd.sqlite3;
EOF
fi

cat > "$root/DEBIAN/control" <<EOF
Package: $package
Version: $debversion
Architecture: $arch
Maintainer: Raphael Henrique <raphaelgaeta@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$root" | cut -f1)
Depends: $depends
Section: $section
Priority: optional
Homepage: https://github.com/raphaelgaeta/Acrux
Description: $summary
 Acrux opens the tabular files that are too big or too messy for Excel:
 Parquet, CSV, Excel workbooks and SQLite databases. It works out the format
 from the file's bytes and types every column by what the data holds, reading
 only what each question needs.
 .
 $about
 .
 The amd64 build needs a CPU with AVX2 (Intel since 2013, AMD since 2015).
EOF

mkdir -p "$outdir"
# xz: a .deb compressed with zstd (Ubuntu's default) doesn't open on Debian 11
dpkg-deb -Zxz --root-owner-group --build "$root" "$outdir/${package}_${debversion}_${arch}.deb"
