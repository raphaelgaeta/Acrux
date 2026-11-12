"""Turns a results JSON of bench.py into the Markdown tables of
docs/benchmarks.md, so the tables are made from the data, never typed.
Usage: python3 summary.py <results.json>

A cell is the median time and the peak memory. A library's time is its own,
in an open session (the import left out), and so is the desktop app's (open
already, it starts nothing per question); a command line's (acrux, duckdb)
is the whole command, startup included. In "pc" every run is cold; in
"full" the median is of the warm runs."""
import json, statistics, sys
from collections import defaultdict

d = json.load(open(sys.argv[1]))
TASKS = {"head10": "First 10 rows, all columns", "screen": "A screenful in memory, ready to scroll",
         "count": "Count the rows", "schema": "Column names and types", "filter_count": "Count rows where a column equals a value",
         "distinct": "Distinct values of a column", "groupby": "Group and sum", "newest10": "The 10 newest rows, all columns"}
COLS = [("pandas", ["pandas"]), ("Polars", ["polars"]), ("DuckDB", ["duckdb"]), ("DuckDB CLI", ["duckdb-cli"]), ("Acrux", ["acrux", "acrux-app"])]
NAIVE = {"pandas": "pandas-naive", "Polars": "polars-naive"}

runs = defaultdict(list)
for r in d["runs"]:
    runs[(r["config"], r["set"], r["task"], r.get("file"), r["tool"])].append(r)

def size(mb):
    return f"{mb / 1024:.1f} GB" if mb >= 1024 else f"{mb} MB"

def cell(config, set_name, task, tool, file=None):
    rs = runs.get((config, set_name, task, file, tool))
    if not rs:
        return "—"
    bad = [r for r in rs if r["status"] != "ok"]
    if bad:
        return f"**out of memory** after {bad[0]['wall_s']:.1f} s" if bad[0]["status"] == "out of memory" else bad[0]["status"]
    use = rs if d["configs"][config]["cold_every_run"] else (rs[1:] or rs)
    # the tool's own time when it reports one (a library, the app); the whole command for a command line
    times = [r.get("op_s", r["wall_s"]) for r in use]
    return f"{statistics.median(times):.2f} s · {size(max(r['peak_mb'] for r in use))}"

for set_name, title in [("wide", "700 columns × 5M rows (9 GB)"), ("tall", "10 columns × 100M rows (2.6 GB)")]:
    for config in d["configs"]:
        print(f"\n#### {title}, {config}\n")
        print("| | " + " | ".join(c for c, _ in COLS) + " |")
        print("|---|" + "---|" * len(COLS))
        for task, label in TASKS.items():
            row = []
            for col, tools in COLS:
                c = next((cell(config, set_name, task, t) for t in tools if (config, set_name, task, None, t) in runs), "—")
                row.append(c)
            print(f"| {label} | " + " | ".join(row) + " |")
            if task == "head10":
                print("| ↳ the whole file first (`read_parquet`) | " + " | ".join(
                    cell(config, set_name, task, NAIVE[c]) if c in NAIVE else "" for c, _ in COLS) + " |")

config = next(iter(d["configs"]))
print(f"\n#### Messy files, no parameters ({config})\n")
print("| file | pandas | Polars | DuckDB | Acrux |\n|---|---|---|---|---|")
files = sorted({r["file"] for r in d["runs"] if r["task"] == "defaults"}, key=lambda f: ["vendas_br.csv", "orders_us.csv", "invoices_us.csv", "loja.db"].index(f))
for f in files:
    row = []
    for tool in ["pandas", "polars", "duckdb", "acrux"]:
        r = runs[(config, "messy", "defaults", f, tool)][0]
        if "error" in r:
            row.append(f"error: {r['error'].split(':')[0]}")
        elif r["status"] != "ok":
            row.append(r["status"])
        else:
            g = r["grade"]
            right = sum(v == "right" for v in g.values())
            notes = [f"**{k} wrong, without an error**" if v == "wrong" else f"{k} as text" for k, v in g.items() if v != "right"]
            row.append(f"{right}/{len(g)} right" + (f" ({', '.join(notes)})" if notes else ""))
    print(f"| `{f}` | " + " | ".join(row) + " |")

print("\n#### `vendas_br.csv`, read right (2M rows)\n")
print("| | pandas | Polars | DuckDB | Acrux |\n|---|---|---|---|---|")
for config in d["configs"]:
    print(f"| {config} | " + " | ".join(cell(config, "messy", "read_right", t, "vendas_br.csv") for t in ["pandas", "polars", "duckdb", "acrux"]) + " |")

print("\nAgreement:", json.dumps(d["agree"]))
print("Versions:", json.dumps(d["versions"]))
print("Machine:", json.dumps(d["machine"]))
