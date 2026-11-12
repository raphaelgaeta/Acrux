"""Records the command-line GIF of the README (assets/cli.gif) for real: runs
each command once to warm the cache, then again, keeping its output and wall
time. The 9 GB test file is not in the repository.
Usage: python3 capture_cli.py <dir with stress_700cols_5Mrows.parquet> <out.json>"""
import json, os, subprocess, sys, time
cwd, out = sys.argv[1], sys.argv[2]
SCENES = [
    ["du -h stress_700cols_5Mrows.parquet",
     "acrux rows stress_700cols_5Mrows.parquet",
     "acrux schema stress_700cols_5Mrows.parquet | head -6",
     "acrux schema stress_700cols_5Mrows.parquet | wc -l"],
    ["acrux column stress_700cols_5Mrows.parquet c615_cat -n 5",
     "acrux head stress_700cols_5Mrows.parquet -n 5 | cut -c1-88"],
    ['acrux filter stress_700cols_5Mrows.parquet c110_ts --last "6 years" -n 5 | cut -c1-88'],
]
env = dict(os.environ, LC_ALL="C", TZ="America/Sao_Paulo")
result = []
for scene in SCENES:
    steps = []
    for cmd in scene:
        subprocess.run(["bash", "-c", cmd], cwd=cwd, env=env, capture_output=True)   # warm the cache once
        t = time.monotonic()
        p = subprocess.run(["bash", "-c", cmd], cwd=cwd, env=env, capture_output=True, text=True)
        steps.append({"cmd": cmd, "seconds": round(time.monotonic() - t, 3), "out": p.stdout.rstrip("\n").split("\n")})
    result.append(steps)
json.dump(result, open(out, "w"), indent=1, ensure_ascii=False)
for scene in result:
    for s in scene:
        print(f"$ {s['cmd']}   [{s['seconds']} s]"); print("\n".join(s["out"]))
