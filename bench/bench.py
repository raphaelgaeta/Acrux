"""The benchmark: the same questions to pandas, Polars, DuckDB (the Python
package and the command line) and Acrux, each run in a process of its own
under a systemd scope with a memory cap and no swap.

    python3 bench.py <data dir> <results dir> [--config pc full] [--sets wide tall messy] [--quick]

The data dir holds what make_stress_parquet.py and make_messy_files.py write:
  wide   stress_700cols_5Mrows.parquet, 700 columns × 5M rows, 9 GB
  tall   tall_10cols_100Mrows.parquet, 10 columns × 100M rows, 2.6 GB
  messy  messy/: CSV and SQLite files with the usual traps, and expected.json.
         Each tool reads each file with no parameter at all, and every column
         is graded right, text (kept as text, nothing lost) or wrong (wrong
         values, without an error). Then vendas_br.csv is timed with the
         parameters that make each tool right.

Configurations:
  pc    an ordinary PC: 4 threads (2 cores with their siblings), 8 GB of
        memory, disk reads at 500 MB/s (a SATA SSD), and the file's pages
        dropped from the cache before every run: 9 GB never stays cached on a
        machine with 8 GB.
  full  the whole laptop: every thread and the NVMe, still under 8 GB; the
        first run cold, then warm runs.

Per run: wall time of the whole command (what a user waits for, startup
included), the tool's own time for the task when it reports one (a library
in an open session), and the peak anonymous memory (RssAnon, sampled every
10 ms; RSS would count the file's cached pages). A run killed at the cap is
"out of memory", and the rest of its runs are skipped. Every tool's answer
is checked against the others.
Needs: the venv of requirements.txt, dotnet, systemd-run, taskset, and the
DuckDB command line at $DUCKDB_CLI (the release's duckdb binary)."""
import argparse, json, os, platform, re, statistics, subprocess, sys, threading, time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from messy import grade, expected_for   # noqa: E402  (pandas is imported only inside its functions)

CONFIGS = {
    "pc":   {"cpus": "0-3", "threads": 4, "read": "500M", "cold_every_run": True, "runs": 3},
    "full": {"cpus": f"0-{os.cpu_count() - 1}", "threads": os.cpu_count(), "read": None, "cold_every_run": False, "runs": 4},
}
MEMORY, DUCKDB_MEMORY = "8G", "6GB"
DATASETS = {"wide": ("stress_700cols_5Mrows.parquet", 700), "tall": ("tall_10cols_100Mrows.parquet", 10)}
CAT, VALUE, TS = "c005_cat", "c003_f64", "c009_ts"
SQL = {
    "head10": "SELECT * FROM read_parquet('{f}') LIMIT 10",
    "count": "SELECT count(*) FROM read_parquet('{f}')",
    "schema": "DESCRIBE SELECT * FROM read_parquet('{f}')",
    "filter_count": f"SELECT count(*) FROM read_parquet('{{f}}') WHERE {CAT} = 'CAT_10'",
    "distinct": f"SELECT DISTINCT {CAT} FROM read_parquet('{{f}}')",
    "groupby": f"SELECT {CAT}, sum({VALUE}) FROM read_parquet('{{f}}') GROUP BY 1",
    "newest10": f"SELECT * FROM read_parquet('{{f}}') ORDER BY {TS} DESC, row_id LIMIT 10",
}

def plan(acrux, probe, duckcli):
    """task -> [(tool, command)]; a command of None is a bench/tasks.py tool"""
    lib = lambda *tools: [(t, None) for t in tools]
    cli = lambda task: [("duckdb-cli", [duckcli, "-csv", "-c", "SET threads = {threads}; SET memory_limit = '" + DUCKDB_MEMORY + "'; " + SQL[task]])]
    return {
        "head10":       lib("pandas-naive", "pandas", "polars-naive", "polars", "duckdb") + cli("head10") + [("acrux", [acrux, "head", "{f}", "-n", "10"])],
        "screen":       lib("pandas", "polars", "duckdb") + [("acrux-app", [probe, "screen", "{f}"])],
        "count":        lib("pandas", "polars", "duckdb") + cli("count") + [("acrux", [acrux, "rows", "{f}"])],
        "schema":       lib("pandas", "polars", "duckdb") + cli("schema") + [("acrux", [acrux, "schema", "{f}"])],
        "filter_count": lib("pandas", "polars", "duckdb") + cli("filter_count") + [("acrux", [acrux, "rows", "{f}", "--where", f"{CAT}=CAT_10"])],
        "distinct":     lib("pandas", "polars", "duckdb") + cli("distinct") + [("acrux", [acrux, "values", "{f}", CAT])],
        "groupby":      lib("pandas", "polars", "duckdb") + cli("groupby") + [("acrux-app", [probe, "groupby", "{f}"])],
        "newest10":     lib("pandas", "polars", "duckdb") + cli("newest10") + [("acrux", [acrux, "filter", "{f}", TS, "--last", "7 years", "-n", "10"])],
    }

def acrux_check(task, out):
    """the command line's answer, read from what it prints"""
    lines = out.rstrip("\n").split("\n")
    def table_ids():
        sep = next(i for i, l in enumerate(lines) if l.startswith("-") and "+" in l)
        rows = [l for l in lines[sep + 1:] if not l.startswith("(")]
        return [int(l.split("|")[0]) for l in rows if "|" in l]
    if task == "head10":
        return {"cols": int(re.search(r"× (\d+) columns", out).group(1)), "row_ids": table_ids()}
    if task in ("count", "filter_count"):
        return int(lines[-1])
    if task == "schema":
        return len([l for l in lines if l.strip()])
    if task == "distinct":
        return {"values": [l for l in lines if l and not l.startswith("(")], "null": "and null" in lines[-1]}
    if task == "newest10":
        return table_ids()
    raise ValueError(task)

def duckcli_check(task, out):
    """the DuckDB command line's answer, from its CSV output (it prints a null as NULL)"""
    rows = [[None if v == "NULL" else v for v in l.split(",")] for l in out.rstrip("\n").split("\n")]
    head, body = rows[0], rows[1:]
    if task == "head10":
        return {"cols": len(head), "row_ids": [int(r[0]) for r in body]}
    if task in ("count", "filter_count"):
        return int(body[0][0])
    if task == "schema":
        return len(body)
    if task == "distinct":
        return {"values": sorted(r[0] for r in body if r[0] is not None), "null": any(r[0] is None for r in body)}
    if task == "groupby":
        return {(r[0] or "null"): float(r[1]) for r in body}
    if task == "newest10":
        return [int(r[0]) for r in body]
    raise ValueError(task)

def drop_cache(path):
    fd = os.open(path, os.O_RDONLY)
    os.posix_fadvise(fd, 0, 0, os.POSIX_FADV_DONTNEED)
    os.close(fd)

def anon_kb(pid):
    try:
        with open(f"/proc/{pid}/status") as f:
            for line in f:
                if line.startswith("RssAnon:"):
                    return int(line.split()[1])
    except OSError:
        pass
    return 0

def tree(pid):
    pids, todo = [], [pid]
    while todo:
        p = todo.pop(); pids.append(p)
        try:
            for t in os.listdir(f"/proc/{p}/task"):
                with open(f"/proc/{p}/task/{t}/children") as f:
                    todo += [int(c) for c in f.read().split()]
        except OSError:
            pass
    return pids

def run_once(cmd, cfg, env, device):
    scope = ["systemd-run", "--user", "--scope", "--quiet", "-p", f"MemoryMax={MEMORY}", "-p", "MemorySwapMax=0"]
    if cfg["read"]:
        scope += ["-p", f"IOReadBandwidthMax={device} {cfg['read']}"]
    full = scope + ["--", "taskset", "-c", cfg["cpus"]] + cmd
    peak, done = [0], threading.Event()
    t0 = time.perf_counter()
    p = subprocess.Popen(full, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
    def sample():
        while not done.is_set():
            peak[0] = max(peak[0], sum(anon_kb(q) for q in tree(p.pid)))
            time.sleep(0.01)
    threading.Thread(target=sample, daemon=True).start()
    try:
        out, err = p.communicate(timeout=900)
    except subprocess.TimeoutExpired:
        p.kill(); out, err = p.communicate()
    wall = time.perf_counter() - t0
    done.set()
    status = "ok" if p.returncode == 0 else "out of memory" if p.returncode == -9 else f"error {p.returncode}"
    return {"wall_s": round(wall, 3), "peak_mb": round(peak[0] / 1024), "status": status, "out": out, "err": err[-600:]}

def same(a, b):
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(same(a[k], b[k]) for k in a)
    if isinstance(a, float) or isinstance(b, float):
        return abs(a - b) <= 1e-9 * max(abs(a), abs(b))
    return a == b

def device_of(path):
    src = re.sub(r"\[.*\]$", "", subprocess.run(["findmnt", "-no", "SOURCE", "-T", path], capture_output=True, text=True).stdout.strip())
    return "/dev/" + (subprocess.run(["lsblk", "-no", "PKNAME", src], capture_output=True, text=True).stdout.strip() or os.path.basename(src))

def log(r):
    print(f"{r['config']:4} {r['set']:5} {r['task']:12} {r.get('file', ''):16} {r['tool']:12} run {r['run']} "
          f"{'cold' if r['cold'] else 'warm'}: {r['status']:13} {r['wall_s']:7.2f} s  op {r.get('op_s', '-')!s:>7}  "
          f"{r['peak_mb']:6} MB" + (f"  {r['grade']}" if "grade" in r else "") + (f"  {r['error']}" if "error" in r else ""), flush=True)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data"); ap.add_argument("results")
    ap.add_argument("--config", nargs="+", default=["pc", "full"])
    ap.add_argument("--sets", nargs="+", default=["wide", "tall", "messy"])
    ap.add_argument("--quick", action="store_true", help="one run each, to try the bench itself")
    a = ap.parse_args()
    data = os.path.abspath(a.data)
    os.makedirs(a.results, exist_ok=True)
    device = device_of(data)
    duckcli = os.environ.get("DUCKDB_CLI") or os.path.expanduser("~/.local/share/acrux-dev/bench-tools/duckdb")

    # the Acrux under test: this checkout, published as the release does (self-contained)
    build = os.path.expanduser("~/.cache/acrux-bench")
    commit = subprocess.run(["git", "-C", REPO, "rev-parse", "--short=12", "HEAD"], capture_output=True, text=True).stdout.strip()
    for proj, out in [("src/Acrux.Cli/Acrux.Cli.csproj", "cli"), ("bench/AcruxProbe/AcruxProbe.csproj", "probe")]:
        subprocess.run(["dotnet", "publish", os.path.join(REPO, proj), "-c", "Release", "-r", "linux-x64", "--self-contained",
                        "-o", os.path.join(build, out), "-v", "q", "-nologo"], check=True, stdout=subprocess.DEVNULL)
    acrux, probe = os.path.join(build, "cli", "Acrux.Cli"), os.path.join(build, "probe", "AcruxProbe")

    py = sys.executable
    subprocess.run([py, "-I", "-c", "import duckdb; duckdb.sql('INSTALL sqlite')"], check=True)   # not inside a timed run
    versions = json.loads(subprocess.run([py, "-c", "import json,sys,pandas,pyarrow,polars,duckdb;print(json.dumps({'python':sys.version.split()[0],'pandas':pandas.__version__,'pyarrow':pyarrow.__version__,'polars':polars.__version__,'duckdb':duckdb.__version__}))"],
                                         capture_output=True, text=True, check=True).stdout)
    versions["duckdb_cli"] = subprocess.run([duckcli, "--version"], capture_output=True, text=True).stdout.strip()
    versions["acrux"] = commit
    versions["polars_net"] = "1.0.1 (Rust Polars 0.55.2)"
    versions["dotnet"] = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
    machine = {"cpu": next(l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name")),
               "threads": os.cpu_count(), "ram_gb": round(os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES") / 2**30),
               "kernel": platform.release()}

    tasks = plan(acrux, probe, duckcli)
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H%MZ")
    results = []
    for name in a.config:
        cfg = CONFIGS[name]
        env = {k: v for k, v in os.environ.items() if k not in ("AI_AGENT", "CLAUDECODE")}   # tools act differently for an agent
        env.update(TZ="America/Sao_Paulo", LC_ALL="C.UTF-8", OMP_NUM_THREADS=str(cfg["threads"]),
                   POLARS_MAX_THREADS=str(cfg["threads"]), BENCH_DUCKDB_MEMORY=DUCKDB_MEMORY)
        runs = 1 if a.quick else cfg["runs"]

        def measure(set_name, task, tool, argv, path, after, extra=None):
            for k in range(runs):
                cold = cfg["cold_every_run"] or k == 0
                if cold:
                    drop_cache(path)
                r = run_once(argv, cfg, env, device)
                r.update(config=name, set=set_name, task=task, tool=tool, run=k, cold=cold, **(extra or {}))
                if r["status"] == "ok":
                    after(r)
                r.pop("out")
                if r["status"] == "ok" and "error" not in r:
                    r.pop("err")
                results.append(r); log(r)
                if r["status"] != "ok" or "error" in r:
                    break   # out of memory once is out of memory every time

        for set_name in [s for s in a.sets if s in DATASETS]:
            file, columns = DATASETS[set_name]
            path = os.path.join(data, file)
            env["BENCH_SCREEN"] = str(min(23, columns))
            for task, tools in tasks.items():
                for tool, cmd in tools:
                    argv = [py, "-I", os.path.join(HERE, "tasks.py"), tool, task, path] if cmd is None else \
                        [c.replace("{f}", path).replace("{threads}", str(cfg["threads"])) for c in cmd]
                    def after(r, tool=tool, cmd=cmd, task=task):
                        if cmd is None or tool == "acrux-app":
                            j = json.loads(r["out"].strip().split("\n")[-1])
                            r["op_s"], r["check"] = j["op_s"], j["check"]
                        else:
                            r["check"] = (duckcli_check if tool == "duckdb-cli" else acrux_check)(task, r["out"])
                    measure(set_name, task, tool, argv, path, after)

        if "messy" in a.sets:
            messy = os.path.join(data, "messy")
            out_parquet = os.path.join(build, "acrux-out.parquet")
            def reader(tool, path, tuned):
                if tool == "acrux":
                    return [probe, "convert", path, out_parquet]
                return [py, "-I", os.path.join(HERE, "messy.py"), tool, path] + (["tuned"] if tuned else [])
            def graded(r, tool, path):
                j = json.loads(r["out"].strip().split("\n")[-1])
                r["op_s"] = j["op_s"]
                if tool == "acrux":
                    if "error" in j["check"]:
                        r["error"] = j["check"]["error"]; return
                    j["digest"] = json.loads(subprocess.run([py, "-I", os.path.join(HERE, "messy.py"), "digest", out_parquet, path],
                                                            capture_output=True, text=True, check=True).stdout)
                if "error" in j:
                    r["error"] = j["error"]; return
                r["grade"] = grade(j["digest"], expected_for(path))
            # does it read right with no parameter? once, in the first configuration
            if name == a.config[0]:
                for file in json.load(open(os.path.join(messy, "expected.json"))):
                    path = os.path.join(messy, file)
                    for tool in ["pandas", "polars", "duckdb", "acrux"]:
                        r = run_once(reader(tool, path, False), cfg, env, device)
                        r.update(config=name, set="messy", task="defaults", file=file, tool=tool, run=0, cold=True)
                        if r["status"] == "ok":
                            graded(r, tool, path)
                        r.pop("out")
                        if r["status"] == "ok" and "error" not in r:
                            r.pop("err")
                        results.append(r); log(r)
            # the time to read the Brazilian CSV right, with the parameters each tool needs
            path = os.path.join(messy, "vendas_br.csv")
            for tool in ["pandas", "polars", "duckdb", "acrux"]:
                measure("messy", "read_right", tool, reader(tool, path, True), path,
                        lambda r, tool=tool: graded(r, tool, path), {"file": "vendas_br.csv"})

    # every answer of a parquet task against the first one that came out
    agree = {}
    for set_name in DATASETS:
        for task in tasks:
            checks = [(r["tool"], r["check"]) for r in results if r["set"] == set_name and r["task"] == task and "check" in r]
            if checks:
                ref = checks[0][1]
                agree[f"{set_name}/{task}"] = sorted({t for t, c in checks if not same(c, ref)}) or "all agree"
    print(json.dumps(agree, indent=1))
    out = os.path.join(a.results, f"results-{stamp}.json")
    json.dump({"when": stamp, "memory_cap": MEMORY, "configs": {n: CONFIGS[n] for n in a.config}, "machine": machine,
               "versions": versions, "agree": agree, "runs": results}, open(out, "w"), indent=1, ensure_ascii=False)
    print(out)

if __name__ == "__main__":
    main()
