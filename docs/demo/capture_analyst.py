"""Records the analyst demo for real: a pandas session working its way through
vendas.csv (make_messy_csv.py), and acrux on the same file.

Each pandas line runs as the Python REPL runs it (compiled in "single" mode, so
an expression prints its repr); an exception keeps its first line and its
message, with the frames between cut to "…" (the full traceback stays in
"raw"). Both sessions run twice and the second run is kept, so the disk cache
is warm on both sides. Each acrux command is a new process, so its time
includes starting .NET; the pandas time starts with the REPL already open.
Usage: python3 capture_analyst.py <dir with vendas.csv> <out.json>"""
import json, os, subprocess, sys

work, out = sys.argv[1], sys.argv[2]

PANDAS = [
    'import pandas as pd',
    'df = pd.read_csv("vendas.csv")',
    'df = pd.read_csv("vendas.csv", encoding="latin-1")',
    'df.head()',
    'df = pd.read_csv("vendas.csv", encoding="latin-1", sep=";")',
    'df["valor"].mean()',
    'df = pd.read_csv("vendas.csv", encoding="latin-1", sep=";", decimal=",")',
    'df["data"] = pd.to_datetime(df["data"])',
    'df["data"] = pd.to_datetime(df["data"], dayfirst=True)',
    'df.dtypes',
    '(df["cidade"] == "Belém").sum()',
]
ACRUX = [
    "acrux schema vendas.csv",
    "acrux head vendas.csv -n 5",
    "acrux rows vendas.csv --where cidade=Belém",
]

REPL = r'''
import contextlib, io, json, sys, time, traceback
ns = {"__name__": "__main__"}
steps = []
for src in json.load(sys.stdin):
    buf, error = io.StringIO(), None
    with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
        t = time.perf_counter()
        try:
            exec(compile(src, "<stdin>", "single"), ns)
        except Exception as e:
            error = e
        seconds = time.perf_counter() - t
    raw = buf.getvalue().rstrip("\n").split("\n") if buf.getvalue() else []
    shown = raw
    if error is not None:
        raw = raw + "".join(traceback.format_exception(error)).rstrip("\n").split("\n")
        shown = shown + ["Traceback (most recent call last):", "  …"] \
            + "".join(traceback.format_exception_only(error)).rstrip("\n").split("\n")
    steps.append({"cmd": src, "seconds": round(seconds, 3), "out": shown, "raw": raw, "error": error is not None})
import pandas
json.dump({"steps": steps, "python": sys.version.split()[0], "pandas": pandas.__version__}, sys.stdout)
'''

env = dict(os.environ, LC_ALL="C.UTF-8", TZ="America/Sao_Paulo", PYTHON_COLORS="0")

def pandas_session():
    p = subprocess.run([sys.executable, "-c", REPL], cwd=work, env=env, input=json.dumps(PANDAS),
                       capture_output=True, text=True, check=True)
    home = os.path.expanduser("~")   # the repository is public: no local paths in the tracebacks
    return json.loads(p.stdout.replace(json.dumps(home)[1:-1], "~"))

def acrux_session():
    steps = []
    for cmd in ACRUX:
        p = subprocess.run(["bash", "-c", f"TIMEFORMAT=%R; time {cmd}"], cwd=work, env=env, capture_output=True, text=True)
        lines = p.stdout.rstrip("\n").split("\n")
        steps.append({"cmd": cmd, "seconds": float(p.stderr.strip().split("\n")[-1].replace(",", ".")),
                      "out": lines, "error": p.returncode != 0})
    return steps

pandas_session(); acrux_session()   # warm the disk cache and the interpreter's .pyc files
py = pandas_session()
ax = acrux_session()
version = subprocess.run(["acrux", "version"], capture_output=True, text=True, env=env).stdout.strip()

result = {
    "width": 785, "height": 598,     # 78 columns × 24 rows each
    "read_ms": [1200, 200, 3500],    # how long an output stays before the next command: the same rule on both sides
    "history": True,                 # a command that starts like an earlier one comes back with ↑ and is finished by hand
    "panes": [
        {"title": f"python3 — pandas {py['pandas']}", "prompt": ">>> ", "scenes": [py["steps"]]},
        {"title": "bash — acrux", "prompt": "$ ", "scenes": [ax]},
    ],
    "versions": {"python": py["python"], "pandas": py["pandas"], "acrux": version},
}
json.dump(result, open(out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
for pane in result["panes"]:
    print(f"== {pane['title']}")
    for s in pane["scenes"][0]:
        print(f"{pane['prompt']}{s['cmd']}   [{s['seconds']} s]")
        print("\n".join(s["out"]))
