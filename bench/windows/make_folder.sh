#!/usr/bin/env bash
# Builds the AcruxBench folder that run.ps1 measures on a Windows PC: the
# desktop app, the command line and the probe of this checkout (win-x64,
# self-contained, as folders: a single file would leave ~300 MB extracted in
# %TEMP%), the DuckDB command line of requirements.txt, run.ps1, verify.ps1
# and SHA256SUMS, which covers the 9 GB test file too. Copy the folder to
# C:\AcruxBench and the test file into its data\, then run verify.ps1.
# Usage: bench/windows/make_folder.sh <out dir> <stress_700cols_5Mrows.parquet>
set -euo pipefail
out=$1/AcruxBench
data=$2
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
duckdb=$(sed -n 's/^duckdb==//p' "$repo/bench/requirements.txt")
rm -rf "$out"
mkdir -p "$out/data" "$out/duckdb"
for p in "src/Acrux.WinForms/Acrux.WinForms.csproj app" "src/Acrux.Cli/Acrux.Cli.csproj cli" "bench/AcruxProbe/AcruxProbe.csproj probe"; do
    set -- $p
    dotnet publish "$repo/$1" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false \
        -p:Version=0.0.0-bench -o "$out/$2" -v q -nologo
done
tmp=$(mktemp -d)
gh release download "v$duckdb" -R duckdb/duckdb -p 'duckdb-cli-windows-amd64.tar.gz' -D "$tmp"
tar -xzf "$tmp/duckdb-cli-windows-amd64.tar.gz" -C "$out/duckdb"
rm -rf "$tmp"
cp "$repo/bench/windows/run.ps1" "$repo/bench/windows/verify.ps1" "$out/"
cd "$out"
find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum | sed 's|  \./|  |' > SHA256SUMS
echo "$(sha256sum < "$data" | cut -d' ' -f1)  data/stress_700cols_5Mrows.parquet" >> SHA256SUMS
echo "$out ($(du -sh . | cut -f1)): copy it to C:\\AcruxBench, and $data into its data\\"
