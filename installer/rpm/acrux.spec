# Acrux for Fedora, built on Copr. It repackages the release's own Linux
# packages, the same files CI tested and SHA256SUMS lists, rather than
# compiling again: .copr/Makefile downloads them for the release tag and
# passes the version (rpmbuild --define "acrux_version 0.3.1").
#
# acrux          the command line, x86_64 and aarch64 (Polars' arm64 native
#                needs glibc 2.39, which every Fedora since 40 has)
# acrux-desktop  the desktop app, x86_64 only, like the release
#
# Both are self-contained: they carry their own .NET runtime, so they don't
# depend on Fedora's dotnet packages.

%{!?acrux_version:%{error:define acrux_version (the release, without the v), as .copr/Makefile does}}

# prebuilt .NET and Rust binaries: nothing to strip or extract debug info from,
# and the CLI and the app carry identical native libraries, whose build-id
# links would collide
%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none

# The bundled libraries are private: don't offer them to the system, and
# don't ask for what only the bundle provides. liblttng-ust is .NET's
# optional tracing provider, which the runtime skips when it's missing.
%global __provides_exclude_from ^%{_libdir}/acrux(-cli)?/.*$
%global __requires_exclude ^(liblttng-ust\\.so\\.0|libmscordaccore\\.so)

Name:           acrux
Version:        %{acrux_version}
Release:        1%{?dist}
Summary:        Read large parquet, CSV, Excel and SQLite files from the command line
# Acrux is MIT; the rest is what it bundles, as scripts/third_party_notices.py
# --spdx prints it for the release (the .NET runtime, Arrow, SQLite, Roslyn,
# the Rust crates inside Polars). Update it when that output changes.
License:        MIT AND Apache-2.0 AND BSD-2-Clause AND BSD-3-Clause AND BSL-1.0 AND CC0-1.0 AND CDLA-Permissive-2.0 AND ISC AND MPL-2.0 AND Unicode-3.0 AND Zlib
URL:            https://github.com/raphaelgaeta/Acrux
Source0:        %{url}/releases/download/v%{version}/acrux-cli-v%{version}-linux-x64.tar.gz
Source1:        %{url}/releases/download/v%{version}/acrux-cli-v%{version}-linux-arm64.tar.gz
Source2:        %{url}/releases/download/v%{version}/Acrux-v%{version}-linux-x64.tar.gz

ExclusiveArch:  x86_64 aarch64
BuildRequires:  desktop-file-utils
# .NET needs ICU for cultures, and %%check runs the command line
BuildRequires:  libicu
Requires:       libicu

%description
Acrux opens the tabular files that are too big or too messy for Excel:
Parquet, CSV, Excel workbooks and SQLite databases. It works out the format
from the file's bytes and types every column by what the data holds, reading
only what each question needs. This is the command line: schema, columns,
the first rows, distinct values, filters and time windows.

The x86_64 build needs a CPU with AVX2 (Intel since 2013, AMD since 2015).

%ifarch x86_64
%package        desktop
Summary:        Desktop viewer for large parquet, CSV, Excel and SQLite files
License:        MIT AND CC-BY-ND-4.0 AND Apache-2.0 AND BSD-2-Clause AND BSD-3-Clause AND BSL-1.0 AND CC0-1.0 AND CDLA-Permissive-2.0 AND ISC AND MPL-2.0 AND Unicode-3.0 AND Zlib
Requires:       libicu
# Avalonia loads these at run time, so rpm can't see them
Requires:       libX11.so.6()(64bit)
Requires:       libXcursor.so.1()(64bit)
Requires:       libXext.so.6()(64bit)
Requires:       libXfixes.so.3()(64bit)
Requires:       libXi.so.6()(64bit)
Requires:       libXrandr.so.2()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)

%description    desktop
The Acrux desktop app: a grid that opens a file of any width at once, with
700 columns or millions of rows, and reads only the columns on the screen.
It filters by header like Excel, follows the system's light, dark and
high-contrast themes, and has a C# terminal for Polars queries. It opens
parquet, CSV, Excel and SQLite files from the menu or with "Open with".

Needs a CPU with AVX2 (Intel since 2013, AMD since 2015).
%endif


%prep
%setup -q -c -T
%ifarch x86_64
tar -xzf %{SOURCE0}
tar -xzf %{SOURCE2}
%endif
%ifarch aarch64
tar -xzf %{SOURCE1}
%endif


%build
# nothing to build: the release's packages are already built


%install
install -d %{buildroot}%{_bindir} %{buildroot}%{_libdir}/acrux-cli
cp -a acrux-cli/. %{buildroot}%{_libdir}/acrux-cli/
# the tarball's own "acrux" link and license files: /usr/bin and %%license
rm %{buildroot}%{_libdir}/acrux-cli/{acrux,LICENSE,THIRD-PARTY-NOTICES.txt,THIRD-PARTY-NOTICES-polars.html}
ln -s ../%{_lib}/acrux-cli/Acrux.Cli %{buildroot}%{_bindir}/acrux

%ifarch x86_64
install -d %{buildroot}%{_libdir}/acrux
cp -a acrux/. %{buildroot}%{_libdir}/acrux/
# install.sh is the per-user install; the package does its work
rm -r %{buildroot}%{_libdir}/acrux/{install.sh,uninstall.sh,share,LICENSE,NOTICE-logo.md,THIRD-PARTY-NOTICES.txt,THIRD-PARTY-NOTICES-polars.html}
ln -s ../%{_lib}/acrux/Acrux %{buildroot}%{_bindir}/acrux-desktop
install -Dpm 0644 acrux/share/acrux.svg %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/acrux.svg
install -Dpm 0644 acrux/share/acrux-parquet.xml %{buildroot}%{_datadir}/mime/packages/acrux-parquet.xml
# the same entry as installer/linux/install.sh writes, same file name, so a
# per-user install in ~/.local takes its place instead of showing twice; the
# MIME types follow the extensions of installer/acrux.iss
install -d %{buildroot}%{_datadir}/applications
cat > %{buildroot}%{_datadir}/applications/acrux.desktop <<'EOF'
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
%endif


%check
%ifarch x86_64
desktop-file-validate %{buildroot}%{_datadir}/applications/acrux.desktop
%endif
# the command line runs here, Polars' native library included: a semicolon
# CSV with a decimal comma, as Excel in Portuguese saves it
%{buildroot}%{_libdir}/acrux-cli/Acrux.Cli version | grep -q '^%{version} '
printf 'nome;valor\nA;1,5\nB;2,25\n' > check.csv
%{buildroot}%{_libdir}/acrux-cli/Acrux.Cli schema check.csv | grep -q 'valor.*f64'


%files
%license acrux-cli/LICENSE acrux-cli/THIRD-PARTY-NOTICES.txt acrux-cli/THIRD-PARTY-NOTICES-polars.html
%{_bindir}/acrux
%{_libdir}/acrux-cli/

%ifarch x86_64
%files desktop
%license acrux/LICENSE acrux/NOTICE-logo.md acrux/THIRD-PARTY-NOTICES.txt acrux/THIRD-PARTY-NOTICES-polars.html
%{_bindir}/acrux-desktop
%{_libdir}/acrux/
%{_datadir}/applications/acrux.desktop
%{_datadir}/icons/hicolor/scalable/apps/acrux.svg
%{_datadir}/mime/packages/acrux-parquet.xml
%endif


%changelog
* Tue Nov 17 2026 Raphael Henrique - 0.3.1-1
- First package: the command line and the desktop app of the 0.3.1 release.
  Each release's notes are on https://github.com/raphaelgaeta/Acrux/releases
