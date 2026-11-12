"""Writes the benchmark's test files. By default 700 columns × 5 million rows,
~9 GB, laid out like the file behind the README's GIFs; with --columns 10
--rows 100000000, the tall one. A row_id, then a cycle of ten
types: i32, i64, f64, f32, a 20-value category with 2% nulls, a text with
12,000 distinct values, a bool with 2% nulls, a date and two timestamps.
ZSTD, row groups of 200,000 rows.

Fixed seeds, one per column per row group: every run writes the same values,
in any order, and the row groups are made in parallel.
Usage: python3 make_stress_parquet.py <out.parquet> [--rows N] [--columns N]   (needs numpy and pyarrow)"""
import argparse, os, sys
from concurrent.futures import ProcessPoolExecutor
import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq

GROUP, SEED = 200_000, 20261111
CYCLE = ["i32", "i64", "f64", "f32", "cat", "str12k", "bool", "date", "ts", "ts"]
CATS = pa.array([f"CAT_{k:02d}" for k in range(20)])
ITEMS = pa.array([f"ITEM_{k:06d}" for k in range(12_000)])
DAY0, DAYS = np.datetime64("2015-01-01", "D"), 3650           # 2015-01-01 to 2024-12-28
HOUR0, HOURS = np.datetime64("2020-01-01T00", "h"), 364 * 24   # 2020-01-01 00:00 to 2020-12-30 23:00
TYPES = {"i32": pa.int32(), "i64": pa.int64(), "f64": pa.float64(), "f32": pa.float32(), "cat": pa.string(),
         "str12k": pa.string(), "bool": pa.bool_(), "date": pa.date32(), "ts": pa.timestamp("us")}

def schema(columns):
    names = [f"c{k:03d}_{CYCLE[(k - 1) % 10]}" for k in range(1, columns)]
    return pa.schema([pa.field("row_id", pa.int64())] + [pa.field(n, TYPES[n.split("_", 1)[1]]) for n in names])

def column(k, group, rows):
    if k == 0:
        return pa.array(np.arange(group * GROUP, group * GROUP + rows, dtype=np.int64))
    rng = np.random.default_rng([SEED, k, group])
    kind = CYCLE[(k - 1) % 10]
    if kind == "i32":
        return pa.array(rng.integers(0, 2**31 - 1, rows, dtype=np.int32))
    if kind == "i64":
        return pa.array(rng.integers(0, 1000, rows, dtype=np.int64))
    if kind == "f64":
        return pa.array(rng.random(rows) * 1_000_000)
    if kind == "f32":
        return pa.array(np.round(rng.random(rows) * 100, 1).astype(np.float32))
    if kind == "cat":
        return CATS.take(pa.array(rng.integers(0, 20, rows), mask=rng.random(rows) < 0.02))
    if kind == "str12k":
        return ITEMS.take(pa.array(rng.integers(0, 12_000, rows)))
    if kind == "bool":
        return pa.array(rng.random(rows) < 0.5, mask=rng.random(rows) < 0.02)
    if kind == "date":
        return pa.array(DAY0 + rng.integers(0, DAYS, rows))
    return pa.array((HOUR0 + rng.integers(0, HOURS, rows)).astype("datetime64[us]"))

def row_group(args):
    group, rows, columns = args
    return pa.Table.from_arrays([column(k, group, rows) for k in range(columns)], schema=schema(columns))

def main():
    p = argparse.ArgumentParser()
    p.add_argument("out")
    p.add_argument("--rows", type=int, default=5_000_000)
    p.add_argument("--columns", type=int, default=700)
    a = p.parse_args()
    jobs = [(g, min(GROUP, a.rows - g * GROUP), a.columns) for g in range((a.rows + GROUP - 1) // GROUP)]
    workers = min(8, os.cpu_count())
    with pq.ParquetWriter(a.out, schema(a.columns), compression="zstd") as w, ProcessPoolExecutor(workers) as pool:
        for start in range(0, len(jobs), workers):   # a batch at a time: a row group is ~0.4 GB in memory
            for n, table in enumerate(pool.map(row_group, jobs[start:start + workers]), start + 1):
                w.write_table(table, row_group_size=GROUP)
                print(f"\rrow group {n}/{len(jobs)}", end="", file=sys.stderr, flush=True)
    print(f"\n{a.out}: {os.path.getsize(a.out) / 1e9:.2f} GB", file=sys.stderr)

if __name__ == "__main__":
    main()
