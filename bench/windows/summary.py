"""The notebook table of docs/benchmarks.md, from the JSON run.ps1 wrote.
Usage: python3 summary.py <laptop-*.json>

Tools are compared on rounds 2-4: round 1 is cold only for the command that
reads the file first after the reboot (Acrux's, in run.ps1's order), so it
appears only as Acrux's first open."""
import json, statistics, sys

d = json.load(open(sys.argv[1], encoding="utf-8-sig"))
runs = d["runs"]

def cell(task, tool, rounds, field="wall_s"):
    rs = [r for r in runs if r["task"] == task and r["tool"] == tool and r["round"] in rounds]
    if not rs:
        return "—"
    return f"{statistics.median(r[field] for r in rs):.2f} s · {max(r['peak_mb'] for r in rs)} MB"

later = {2, 3, 4}
rows = [("First 10 rows, all columns", "head10", "wall_s"), ("The 10 newest rows, all columns", "newest10", "wall_s"),
        ("Count the rows", "count", "wall_s")]
print("| | DuckDB CLI | Acrux | Acrux, first after the reboot |\n|---|---|---|---|")
for label, task, field in rows:
    print(f"| {label} | {cell(task, 'duckdb-cli', later)} | {cell(task, 'acrux', later)} | {cell(task, 'acrux', {1})} |")
print(f"| The desktop app's first screen, data only | — | {cell('screen', 'acrux-app', later, 'op_s')} | {cell('screen', 'acrux-app', {1}, 'op_s')} |")
i = d["info"]
free = sorted({r["free_gb_before_round"] for r in runs})
print(f"\n{i['cpu']}, {i['threads']} threads, {i['ram_gb']} GB ({free[0]}–{free[-1]} GB free), {i['windows']}, "
      f"Defender real-time {'on' if i['defender_realtime'] else 'off'}, on AC: {i['on_ac']}, {i['minutes_since_boot']} min after boot; "
      f"Acrux {i['acrux']}, DuckDB {i['duckdb']}")
