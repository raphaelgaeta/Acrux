"""The messy files (make_messy_files.py), read by a Python tool in a process of
its own:
    python3 messy.py <tool> <file> [tuned]    read it; print op_s and the digest
    python3 messy.py digest <file.parquet> <original file>
                                              the digest of the parquet Acrux wrote
tool: pandas, polars, duckdb. Without "tuned", the reader's defaults and no
parameter at all: does it read the file right on its own? "tuned" is what
someone who knows the file writes (vendas_br.csv only), to time it: the
encoding, the separator, the decimal mark, the date format spelled out and
the type of the column that changes 200,000 rows in. Spelled out, not
inferred: Polars' try_parse_dates took 26 of its 28 s on this file.

The digest is computed the same way for every tool: per column, its kind
(number, date, instant, text, mixed) and the checks expected.json asks for.
grade() turns it into right / text (kept as text, nothing lost) / wrong
(converted to wrong values or lost, without an error)."""
import json, os, sys, time

def read(tool, path, tuned):
    name = os.path.basename(path)
    if name.endswith(".db"):
        import sqlite3
        if tool == "pandas":
            import pandas as pd
            return pd.read_sql("SELECT * FROM vendas", sqlite3.connect(path))
        if tool == "polars":
            import polars as pl
            return pl.read_database("SELECT * FROM vendas", sqlite3.connect(path))
        import duckdb
        return duckdb.connect().sql(f"SELECT * FROM sqlite_scan('{path}', 'vendas')").to_arrow_table()
    if tool == "pandas":
        import pandas as pd
        if tuned:
            return pd.read_csv(path, encoding="cp1252", sep=";", decimal=",", parse_dates=["data"], date_format="%d/%m/%Y",
                               dtype={"desconto": "float64"})
        return pd.read_csv(path)
    if tool == "polars":
        import polars as pl
        if tuned:   # Polars reads UTF-8 only: transcode first ("utf8-lossy" would turn the accents into �)
            with open(path, encoding="cp1252") as f:
                return pl.read_csv(f.read().encode(), separator=";", decimal_comma=True, schema_overrides={"desconto": pl.Float64}) \
                    .with_columns(pl.col("data").str.to_date("%d/%m/%Y"))
        return pl.read_csv(path)
    import duckdb
    con = duckdb.connect(config={"threads": len(os.sched_getaffinity(0)), "memory_limit": os.environ.get("BENCH_DUCKDB_MEMORY", "6GB")})
    if tuned:
        return con.sql(f"SELECT * FROM read_csv('{path}', encoding = 'latin-1', delim = ';', decimal_separator = ',', "
                       "dateformat = '%d/%m/%Y', types = {'desconto': 'DOUBLE'})").to_arrow_table()
    return con.sql(f"SELECT * FROM read_csv('{path}')").to_arrow_table()

def kinds(result):
    """{column: kind} from the tool's own types"""
    out = {}
    if hasattr(result, "schema") and hasattr(result.schema, "types"):   # Arrow
        import pyarrow.types as t
        for f in result.schema:
            k = f.type
            out[f.name] = ("number" if t.is_integer(k) or t.is_floating(k) or t.is_decimal(k) else "date" if t.is_date(k)
                           else ("instant" if k.tz else "date") if t.is_timestamp(k) else "text" if t.is_string(k) or t.is_large_string(k)
                           or t.is_string_view(k) else "bool" if t.is_boolean(k) else str(k))
    elif hasattr(result, "schema"):   # Polars
        import polars as pl
        for name, k in result.schema.items():
            out[name] = ("number" if k.is_numeric() else "date" if k == pl.Date else ("instant" if k.time_zone else "date")
                         if isinstance(k, pl.Datetime) else "text" if k == pl.String else "bool" if k == pl.Boolean else str(k))
    else:   # pandas
        import pandas as pd
        for name in result.columns:
            s = result[name]
            if pd.api.types.is_bool_dtype(s): out[name] = "bool"
            elif pd.api.types.is_numeric_dtype(s): out[name] = "number"
            elif isinstance(s.dtype, pd.DatetimeTZDtype): out[name] = "instant"
            elif pd.api.types.is_datetime64_dtype(s): out[name] = "date"
            else:
                types = set(map(type, s.dropna()))
                out[name] = "text" if types <= {str} else "mixed"
    return out

def digest(result, expected):
    import pandas as pd
    k = kinds(result)
    df = result if isinstance(result, pd.DataFrame) else result.to_pandas()
    epoch = pd.Timestamp("1970-01-01")
    cols = {}
    for name, exp in expected["columns"].items():
        if name not in df.columns:
            continue
        s, kind = df[name], k[name]
        d = {"kind": kind, "nonnull": int(s.notna().sum())}
        if kind == "number":
            d["sum"] = float(s.sum())
        elif kind == "date":
            d["days"] = int((pd.to_datetime(s).dt.normalize() - epoch).dt.days.sum())
        elif kind == "instant":
            d["seconds"] = int((s - pd.Timestamp("1970-01-01", tz="UTC")).dt.total_seconds().sum())
        if "sentinel" in exp:
            d["count"] = int((s.astype(object) == exp["sentinel"]).sum())
        cols[name] = d
    return {"rows": len(df), "columns": cols}

def grade(dg, expected):
    """{column: right | text | wrong}"""
    out = {}
    for name, exp in expected["columns"].items():
        got = dg["columns"].get(name)
        e = exp["kind"]
        if got is None:
            out[name] = "wrong"
        elif e == "number" and got["kind"] == "number":
            out[name] = "right" if abs(got["sum"] - exp["sum"]) <= 1e-6 * max(1, abs(exp["sum"])) else "wrong"
        elif e == "date" and got["kind"] == "date":
            out[name] = "right" if got["days"] == exp["days"] else "wrong"
        elif e == "instant" and got["kind"] == "instant":
            out[name] = "right" if got["seconds"] == exp["seconds"] else "wrong"
        elif e == "text":
            out[name] = "right" if got["kind"] == "text" and got.get("count") == exp["count"] else "wrong"
        elif e == "mixed":
            out[name] = "right" if got["kind"] in ("text", "mixed") and got["nonnull"] == exp["nonnull"] \
                and got.get("count") == exp["count"] else "wrong"
        elif got["kind"] == "text" and got["nonnull"] == expected["rows"]:
            out[name] = "text"
        else:
            out[name] = "wrong"
    return out

def expected_for(path):
    return json.load(open(os.path.join(os.path.dirname(os.path.abspath(path)), "expected.json")))[os.path.basename(path)]

if __name__ == "__main__":
    if sys.argv[1] == "digest":
        import pyarrow.parquet as pq
        print(json.dumps(digest(pq.read_table(sys.argv[2]), expected_for(sys.argv[3]))))
    else:
        tool, path, tuned = sys.argv[1], sys.argv[2], len(sys.argv) > 3 and sys.argv[3] == "tuned"
        exp = expected_for(path)
        __import__({"pandas": "pandas", "polars": "polars", "duckdb": "duckdb"}[tool])   # before the clock
        t = time.perf_counter()
        try:
            result = read(tool, path, tuned)
        except Exception as e:
            print(json.dumps({"op_s": round(time.perf_counter() - t, 4), "error": f"{type(e).__name__}: {str(e).splitlines()[0][:200]}"}))
            sys.exit(0)
        op = time.perf_counter() - t
        print(json.dumps({"op_s": round(op, 4), "digest": digest(result, exp)}))
