"""What the desktop app's first screen needs on the tall file (10 columns ×
100M rows), where it ran out of memory at 8 GB in bench.py.

  1. Memory caps of 8, 10, 12 and 14 GB, three runs each, until a tool holds
     the screen (the 10 whole columns) in all three: Acrux as the app loads it
     today (every column of the screen at once), Acrux one column at a time,
     Polars, DuckDB and pandas. Near its need a tool fits in some runs and not
     in others, so a cap counts only when all three fit.
  2. Acrux loading the screen k columns at a time, k = 1, 2, 3, 4, 6, all,
     on the tall and on the wide file, under a 13 GB cap so none fails:
     the time and the peak, to find the sweet spot.
Ordinary-PC config for 1; both configs for 2. Writes results/screen-memory-<when>.json
and prints the tables.
Usage: python3 screen_memory.py <data dir> <results dir> [caps] [batches]   (after bench.py, which builds the probe)"""
import json, os, statistics, sys
from datetime import datetime, timezone
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bench

data, results = os.path.abspath(sys.argv[1]), sys.argv[2]
parts = sys.argv[3:] or ["caps", "batches"]
tall = os.path.join(data, "tall_10cols_100Mrows.parquet")
wide = os.path.join(data, "stress_700cols_5Mrows.parquet")
probe = os.path.expanduser("~/.cache/acrux-bench/probe/AcruxProbe")
py = sys.executable
device = bench.device_of(tall)

def env_for(cfg, **extra):
    env = {k: v for k, v in os.environ.items() if k not in ("AI_AGENT", "CLAUDECODE")}
    env.update(TZ="America/Sao_Paulo", LC_ALL="C.UTF-8", OMP_NUM_THREADS=str(cfg["threads"]),
               POLARS_MAX_THREADS=str(cfg["threads"]), **extra)
    return env

def run(cmd, cfg, env, path, cold):
    if cold:
        bench.drop_cache(path)
    r = bench.run_once(cmd, cfg, env, device)
    r["op_s"] = json.loads(r["out"].strip().split("\n")[-1])["op_s"] if r["status"] == "ok" else None
    return {k: r[k] for k in ("status", "wall_s", "op_s", "peak_mb")}

out = {"caps": [], "batches": []}
pc = bench.CONFIGS["pc"]
tools = {
    "Acrux, all at once (today)": [probe, "screen", tall],
    "Acrux, one at a time": [probe, "screen-batch", tall, "1"],
    "Polars": [py, "-I", os.path.join(HERE, "tasks.py"), "polars", "screen", tall],
    "DuckDB": [py, "-I", os.path.join(HERE, "tasks.py"), "duckdb", "screen", tall],
    "pandas": [py, "-I", os.path.join(HERE, "tasks.py"), "pandas", "screen", tall],
}
for name, cmd in (tools.items() if "caps" in parts else []):
    for cap in [8, 10, 12, 14]:
        bench.MEMORY = f"{cap}G"
        rs = [run(cmd, pc, env_for(pc, BENCH_SCREEN="10", BENCH_DUCKDB_MEMORY=f"{int(cap * 0.75)}GB"), tall, True) for _ in range(3)]
        out["caps"] += [{"tool": name, "cap_gb": cap, "run": k, **r} for k, r in enumerate(rs)]
        print(name, cap, [(r["status"], r["peak_mb"]) for r in rs], flush=True)
        if all(r["status"] == "ok" for r in rs):
            break

bench.MEMORY = "13G"
for config in (["pc", "full"] if "batches" in parts else []):
    cfg = bench.CONFIGS[config]
    for file, path, ks in [("tall", tall, [1, 2, 3, 4, 6, 10]), ("wide", wide, [1, 2, 3, 4, 6, 23])]:
        for k in ks:
            rs = [run([probe, "screen-batch", path, str(k)], cfg, env_for(cfg), path, cfg["cold_every_run"] or rep == 0) for rep in range(3)]
            use = rs if cfg["cold_every_run"] else rs[1:]
            line = {"config": config, "file": file, "k": k, "all": k == ks[-1],
                    "op_s": statistics.median(r["op_s"] for r in use), "peak_mb": max(r["peak_mb"] for r in use)}
            out["batches"].append(line); print(line, flush=True)

stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H%MZ")
json.dump(out, open(os.path.join(results, f"screen-memory-{stamp}.json"), "w"), indent=1)

if out["caps"]:
    print("\n| runs that held it, of 3 | 8 GB | 10 GB | 12 GB | 14 GB |\n|---|---|---|---|---|")
    for name in tools:
        cells = []
        for cap in [8, 10, 12, 14]:
            rs = [r for r in out["caps"] if r["tool"] == name and r["cap_gb"] == cap]
            ok = [r for r in rs if r["status"] == "ok"]
            cells.append("" if not rs else f"{len(ok)}/3" + (f" · {max(r['peak_mb'] for r in ok) / 1024:.1f} GB, {statistics.median(r['op_s'] for r in ok):.1f} s" if ok else ""))
        print(f"| {name} | " + " | ".join(cells) + " |")
if out["batches"]:
    print("\n| columns loading at once | " + " | ".join(str(k) for k in [1, 2, 3, 4, 6]) + " | all |\n|---|" + "---|" * 6)
for config in (["pc", "full"] if out["batches"] else []):
    for file in ["tall", "wide"]:
        cells = [f"{b['op_s']:.2f} s · {b['peak_mb'] / 1024:.1f} GB" for b in out["batches"] if b["config"] == config and b["file"] == file]
        print(f"| {file}, {config} | " + " | ".join(cells) + " |")
