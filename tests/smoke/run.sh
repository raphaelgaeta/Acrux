#!/usr/bin/env bash
# Smoke test for the CLI: runs every command over small fixtures and compares
# the output, exit codes included, with expected.txt.
#   tests/smoke/run.sh <acrux binary>            exit 0 = same output
#   tests/smoke/run.sh <acrux binary> --update   rewrites expected.txt (review the diff!)
# Needs sqlite3 (builds banco.db from banco.sql).
set -uo pipefail

[[ $# -ge 1 ]] || { echo "usage: $0 <acrux binary> [--update]" >&2; exit 2; }
acrux=$(realpath "$1")
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp "$here"/fixtures/{vendas.csv,eua.csv,tipos.parquet,planilha.xlsx} "$work"
sqlite3 "$work/banco.db" < "$here/fixtures/banco.sql"
# Windows-1252, the way Excel's plain "CSV" saves it (kept out of git as bytes)
printf 'cidade;valor\nS\xe3o Paulo;1,5\nA\xe7ail\xe2ndia;2\n' > "$work/ansi.csv"
# the format comes from the bytes, not the name
cp "$work/vendas.csv" "$work/sem_extensao"
cp "$work/banco.db" "$work/dados.csv"

export TZ=America/Sao_Paulo   # zoned times are shown in the local zone
unset ACRUX_DEBUG
cd "$work" || exit 1

# Masked: the filter header (holds the current time) and the temp folder in
# error messages. Everything else must match.
run() {
    printf '$ acrux %s\n' "$*"
    "$acrux" "$@" 2>&1 | sed -E -e 's/^from .* to .* \(--last/from <since> to <now> (--last/' -e "s|$work|<work>|g"
    printf '[exit %d]\n\n' "${PIPESTATUS[0]}"
}

{
    for f in vendas.csv eua.csv ansi.csv sem_extensao tipos.parquet planilha.xlsx banco.db dados.csv; do
        run type "$f"
    done
    for f in vendas.csv eua.csv ansi.csv tipos.parquet planilha.xlsx banco.db; do
        run schema "$f"
        run rows "$f"
    done
    run columns banco.db
    run head vendas.csv
    run head eua.csv
    run head ansi.csv
    run head tipos.parquet -n 3
    run head banco.db -n 2
    run column banco.db data -n 3
    run column vendas.csv Produto
    run filter banco.db data --last "100 years"
    run filter vendas.csv produto --last "1 day"
    run filter banco.db data --last "1 parsec"
    run head nao_existe.csv
    run head vendas.csv -n 0
} > "$work/actual.txt"

if [[ ${2:-} == --update ]]; then
    cp "$work/actual.txt" "$here/expected.txt"
    echo "expected.txt rewritten: review it with git diff"
    exit 0
fi
diff -u "$here/expected.txt" "$work/actual.txt" && echo "smoke test: ok"
