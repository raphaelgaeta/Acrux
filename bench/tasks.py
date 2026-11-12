"""One benchmark task with one Python tool, in a process of its own:
    python3 tasks.py <tool> <task> <file.parquet>
tool: pandas, pandas-naive, polars, polars-naive, duckdb. The naive ones are
what people type first (load the whole file); the others are each tool's
best use, as someone who knows it would write it.

Prints one JSON line: "op_s", the seconds spent in the task (the import is
left out), and "check", a digest of the answer that every tool, Acrux
included, must agree on. bench.py compares them."""
import json, os, sys, time

tool, task, path = sys.argv[1:4]
THREADS = len(os.sched_getaffinity(0))
CYCLE = ["i32", "i64", "f64", "f32", "cat", "str12k", "bool", "date", "ts", "ts"]
SCREEN = ["row_id"] + [f"c{k:03d}_{CYCLE[(k - 1) % 10]}"   # what the app reads for its first screen: 23 columns, or all of a narrower file
                       for k in range(1, int(os.environ.get("BENCH_SCREEN", "23")))]
CAT, VALUE, TS = "c005_cat", "c003_f64", "c009_ts"

def ids(t):
    """the row_id column of a pandas, Polars or Arrow table, as ints"""
    c = t["row_id"]
    return [int(x) for x in (c.to_pylist() if hasattr(c, "to_pylist") else c.to_list() if hasattr(c, "to_list") else c.tolist())]

def checks(kind, value):
    """value is a pandas, Polars or Arrow table, or a plain answer"""
    return {"head10": lambda t: {"cols": len(t.columns), "row_ids": ids(t)},
            "screen": lambda t: {"cols": len(t.columns), "rows": len(t)},
            "newest10": ids}.get(kind, lambda v: v)(value)

def sums(pairs):
    return {("null" if k is None or k != k else str(k)): float(v) for k, v in pairs}

def run_pandas():
    import pandas as pd, pyarrow.parquet as pq
    if tool == "pandas-naive":   # the whole file, then the question
        df = pd.read_parquet(path)
        return {"head10": lambda: df.head(10)}[task]()
    return {
        "head10": lambda: next(pq.ParquetFile(path).iter_batches(batch_size=10)).to_pandas(),
        "screen": lambda: pd.read_parquet(path, columns=SCREEN),
        "count": lambda: pq.ParquetFile(path).metadata.num_rows,
        "schema": lambda: len(pq.read_schema(path)),
        "filter_count": lambda: len(pd.read_parquet(path, columns=[CAT], filters=[(CAT, "==", "CAT_10")])),
        "distinct": lambda: (lambda s: {"values": sorted(s.dropna().unique().tolist()), "null": bool(s.isna().any())})(
            pd.read_parquet(path, columns=[CAT])[CAT]),
        "groupby": lambda: sums(pd.read_parquet(path, columns=[CAT, VALUE]).groupby(CAT, dropna=False)[VALUE].sum().items()),
        # two passes: the time column alone finds the cut, then only the rows past it, with every column
        "newest10": lambda: (lambda cut: pd.read_parquet(path, filters=[(TS, ">=", cut)])
                             .sort_values([TS, "row_id"], ascending=[False, True]).head(10))(
            pd.read_parquet(path, columns=[TS])[TS].nlargest(10).min()),
    }[task]()

def run_polars():
    import polars as pl
    if tool == "polars-naive":
        df = pl.read_parquet(path)
        return {"head10": lambda: df.head(10)}[task]()
    lf = pl.scan_parquet(path)
    return {
        "head10": lambda: lf.head(10).collect(),
        "screen": lambda: lf.select(SCREEN).collect(),
        "count": lambda: lf.select(pl.len()).collect().item(),
        "schema": lambda: len(lf.collect_schema()),
        "filter_count": lambda: lf.filter(pl.col(CAT) == "CAT_10").select(pl.len()).collect().item(),
        "distinct": lambda: (lambda s: {"values": sorted(s.drop_nulls().to_list()), "null": s.null_count() > 0})(
            lf.select(pl.col(CAT).unique()).collect()[CAT]),
        "groupby": lambda: sums(lf.group_by(CAT).agg(pl.col(VALUE).sum()).collect().iter_rows()),
        "newest10": lambda: lf.sort([TS, "row_id"], descending=[True, False]).head(10).collect(),
    }[task]()

def run_duckdb():
    import duckdb
    con = duckdb.connect(config={"threads": THREADS, "memory_limit": os.environ.get("BENCH_DUCKDB_MEMORY", "6GB")})
    src = f"read_parquet('{path}')"
    q = lambda sql: con.sql(sql).to_arrow_table()
    one = lambda sql: con.execute(sql).fetchone()[0]
    return {
        "head10": lambda: q(f"SELECT * FROM {src} LIMIT 10"),
        "screen": lambda: q(f"SELECT {', '.join(SCREEN)} FROM {src}"),
        "count": lambda: one(f"SELECT count(*) FROM {src}"),
        "schema": lambda: len(con.sql(f"DESCRIBE SELECT * FROM {src}").fetchall()),
        "filter_count": lambda: one(f"SELECT count(*) FROM {src} WHERE {CAT} = 'CAT_10'"),
        "distinct": lambda: (lambda vs: {"values": sorted(v for v in vs if v is not None), "null": None in vs})(
            [r[0] for r in con.sql(f"SELECT DISTINCT {CAT} FROM {src}").fetchall()]),
        "groupby": lambda: sums(con.sql(f"SELECT {CAT}, sum({VALUE}) FROM {src} GROUP BY 1").fetchall()),
        "newest10": lambda: q(f"SELECT * FROM {src} ORDER BY {TS} DESC, row_id LIMIT 10"),
    }[task]()

runner = {"pandas": run_pandas, "pandas-naive": run_pandas, "polars": run_polars,
          "polars-naive": run_polars, "duckdb": run_duckdb}[tool]
if tool.startswith("pandas"):
    import pandas, pyarrow   # imported before the clock starts, like the others
    pyarrow.set_cpu_count(THREADS); pyarrow.set_io_thread_count(THREADS)
elif tool.startswith("polars"):
    import polars
else:
    import duckdb
t = time.perf_counter()
answer = runner()
op = time.perf_counter() - t
print(json.dumps({"op_s": round(op, 4), "check": checks(task, answer)}))
