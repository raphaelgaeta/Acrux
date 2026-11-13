# Benchmark

The same questions to pandas, Polars, DuckDB (the Python package and the
command line) and Acrux. The results and what they mean are in
[docs/benchmarks.md](../docs/benchmarks.md); this folder repeats them.

```
python3 -m venv .venv && .venv/bin/pip install -r bench/requirements.txt
D=/data/acrux-bench
.venv/bin/python bench/make_stress_parquet.py $D/stress_700cols_5Mrows.parquet                             # 9 GB, ~2 min
.venv/bin/python bench/make_stress_parquet.py $D/tall_10cols_100Mrows.parquet --columns 10 --rows 100000000 # 2.6 GB, ~30 s
.venv/bin/python bench/make_messy_files.py $D/messy                                                        # ~15 s
DUCKDB_CLI=/path/to/duckdb .venv/bin/python bench/bench.py $D bench/results                                # ~15 min
.venv/bin/python bench/summary.py bench/results/results-<when>.json                                        # the tables
.venv/bin/python bench/screen_memory.py $D bench/results                                                   # ~15 min
```

Every file comes out the same on every run (fixed seeds). With the pyarrow
and numpy of `requirements.txt`, the SHA-256 of the 9 GB file was
`28fef26a7ccff9c5037e57159eaf4a64b33e90e77153bad088c60e65755a218c` and that
of the tall one `711e096a1403f70f0bf81683f5da8e0b841101dfc59aba0beae948ac7acb8e52`.

On a Windows PC, with no Python and nothing installed:

```
bench/windows/make_folder.sh /tmp/out $D/stress_700cols_5Mrows.parquet   # ~900 MB, then copy it and the 9 GB file
powershell -NoProfile -ExecutionPolicy Bypass -File C:\AcruxBench\verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File C:\AcruxBench\run.ps1   # right after a reboot
```

It needs Linux with systemd (user scopes, with the memory and io controllers
delegated, as Fedora does), `taskset`, the .NET 10 SDK, the DuckDB command
line of the version in `requirements.txt` (`duckdb-cli-linux-amd64.tar.gz` of
its GitHub release) and ~13 GB of disk. `bench.py` publishes the Acrux of the
checkout it runs from, the way the release does (self-contained), so a
result belongs to a commit.

| file | what it does |
|---|---|
| `make_stress_parquet.py` | writes the parquet files: a row id, then a cycle of i32, i64, f64, f32, a category with nulls, a text with 12,000 values, a bool with nulls, a date and two timestamps |
| `make_messy_files.py` | writes the messy files (a CSV as Excel saves it in Portuguese, two US CSVs, a SQLite database with mixed types) and `expected.json`, the truth about each column |
| `tasks.py` | one question about a parquet file, one Python tool, in a process of its own; prints the tool's own time and a digest of the answer |
| `messy.py` | one messy file read by one Python tool, with no parameters or with the right ones; digests and grades every column the same way for every tool |
| `AcruxProbe/` | what the command line doesn't do, through Acrux.Core as the desktop app runs it: the first screen of the grid, opening a CSV or SQLite file, and the C# terminal's `GroupBy` |
| `bench.py` | runs everything under a memory cap, measures time and peak memory, and checks every answer against the others |
| `summary.py` | the Markdown tables of docs/benchmarks.md, from a results JSON |
| `screen_memory.py` | what the desktop app's first screen needs on the tall file: the memory cap it fits in, three runs per cap, and the time and peak loading k columns at a time |
| `windows/` | the check on a real Windows PC: `make_folder.sh` builds the folder (the app, the command line, the probe, DuckDB's command line, `SHA256SUMS`), `verify.ps1` checks the copy, `run.ps1` measures, `summary.py` makes the table |
| `results/` | one JSON per run of `bench.py`, `screen_memory.py` or `windows/run.ps1`, with the machine and the versions |
