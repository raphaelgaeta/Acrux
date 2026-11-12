# Benchmarks

Acrux next to the tools people usually open these files with: pandas,
Polars and DuckDB (the Python package and the command line). The same
questions, on the same files and machine, with every answer checked against
the others. It runs from [`bench/`](../bench), and anyone can repeat it.

Measured on 2026-11-12, Acrux at commit `14b3a6a` (Polars.NET 1.0.1).

## The short version

- **Messy files, no parameters: Acrux reads them right, and never reads one
  wrong without a word.** A CSV the way Excel saves it in Portuguese
  (Windows-1252, `;`, decimal commas, day/month dates) stopped pandas, Polars
  and DuckDB; Acrux got all six columns right. So did a SQLite database with
  mixed types, where Polars and DuckDB failed. DuckDB was the only tool to
  read a column wrong silently: US dates whose order nothing in the file
  proves, read day first. It was also the only one to beat Acrux on a messy
  file, by taking the order of the dates from another column.
- **A wide file on an ordinary PC: Acrux opens it with the least memory.**
  On the 700-column, 9 GB file, with 8 GB of memory, the obvious
  `read_parquet` of pandas and Polars runs out of memory. `acrux head` shows
  the first rows in 0.8 s with 41 MB, where the others take 400 to 800 MB,
  and the desktop app's first screen is the fastest of the four.
- **A tall file is Acrux's weak spot.** With 100 million rows, the desktop
  app holds every column of the screen whole: today its first screen needs
  10 to 12 GB, where Polars and DuckDB hold the same columns in 7.3. Loading
  one column at a time brings it to 8 GB, still at the edge (below); a file
  that tall on a PC with less free memory needs the grid to load row ranges
  instead. DuckDB is the best tool on the tall file overall.
- **Raw speed is not the point.** Acrux runs on Polars, so it can't be faster
  than Polars in the hands of someone who knows it. What it adds is getting
  there without knowing the shortcuts, and a grid to scroll and filter.

## How it was measured

One laptop, two configurations:

| | ordinary PC (`pc`) | whole laptop (`full`) |
|---|---|---|
| threads | 4 (2 cores and their siblings, `taskset`) | 32 |
| memory | 8 GB, no swap | 8 GB, no swap |
| disk reads | 500 MB/s, a SATA SSD (cgroup `IOReadBandwidthMax`) | the NVMe, ~4.7 GB/s |
| disk cache | dropped before every run: 9 GB never stays cached in 8 GB | the first run cold, then warm |

The laptop is an Intel i9-13900HX (32 threads, 31 GB, NVMe) on Fedora 44,
Linux 7.2. The ordinary PC is emulated, and the emulation leaves out what a
real one has: a slower CPU (these are 5.4 GHz cores), an antivirus reading
the file and memory on one channel. A run on a real 2017 laptop with 8 GB is
planned as a check.

- **Every run is a process of its own,** under a systemd scope with the
  memory cap and no swap. A run killed at the cap is "out of memory".
- **Time:** a library's own time for the task, in an open session (the
  `import` left out), and so is the desktop app's, which is open already. A
  command line (`acrux`, `duckdb`) is timed as the whole command, startup
  included, which counts against both. The median of 3 runs (`pc`) or of 3
  warm runs (`full`).
- **Memory:** the peak anonymous memory of the process (`RssAnon`, sampled
  every 10 ms). RSS would count the file's cached pages.
- **Each tool at its best, plus what people type first.** The "whole file
  first" row is `read_parquet` with no columns; the rest is each tool used
  the way someone who knows it would. Where a tool ran out of memory, its
  expert variants were tried too: for the 10 newest rows, Polars' `top_k`
  and its streaming engine, and a pandas loop over row groups. They ran out
  the same way (the allocator of pyarrow was not tuned).
- **Every answer agrees,** on all 16 parquet questions: row ids, counts,
  distinct values, sums to 9 significant digits.

| | version |
|---|---|
| Acrux | `14b3a6a`, Polars.NET 1.0.1 (Rust Polars 0.55.2), .NET 10 |
| pandas | 3.1.1, pyarrow 26.0.1 |
| Polars | 2.1.0 (a newer release of the same engine) |
| DuckDB | 2.0.0, Python and the command line |
| Python | 3.14.8 |

## The files

All generated with fixed seeds by [`bench/`](../bench), the same bytes on
every run.

- **Wide:** 700 columns × 5 million rows, 9 GB of Parquet (ZSTD, row groups
  of 200,000). A row id, then a cycle of ten types: i32, i64, f64, f32, a
  20-value category with 2% nulls, a text with 12,000 distinct values, a
  bool with 2% nulls, a date and two timestamps. The same layout as the file
  in the README's animations.
- **Tall:** the first 10 of those columns × 100 million rows, 2.6 GB.
- **Messy:**
  - `vendas_br.csv`, 2M rows: Excel's CSV on a Portuguese-language Windows.
    Windows-1252, `;`, decimal commas, day/month dates, the first one
    (03/02/2026) readable both ways. A discount column holds whole numbers
    for 200,000 rows, then decimals too.
  - `orders_us.csv`, 1M rows, UTF-8, month/day dates. `order_date` proves its
    order (days past 12); `ship_date` never goes past day 12.
  - `invoices_us.csv`, 500,000 rows, UTF-8. Its only date column never goes
    past day 12: nothing in the file proves the order.
  - `loja.db`, SQLite, 300,000 rows. A REAL column and an INTEGER column with
    some text in them (SQLite allows it), a column with no declared type,
    and ISO 8601 times with a UTC offset.

## Results

Each cell is the median time and the peak memory.

### Ordinary PC

#### 700 columns × 5M rows (9 GB)

| | pandas | Polars | DuckDB | DuckDB CLI | Acrux |
|---|---|---|---|---|---|
| First 10 rows, all columns | 2.98 s · 818 MB | 0.75 s · 402 MB | 1.10 s · 605 MB | 0.91 s · 598 MB | 0.77 s · 41 MB |
| ↳ the whole file first (`read_parquet`) | **out of memory** after 7.0 s | **out of memory** after 70.3 s |  |  |  |
| A screenful in memory, ready to scroll | 1.06 s · 2.1 GB | 0.85 s · 1.0 GB | 0.94 s · 1.3 GB | — | 0.60 s · 1019 MB |
| Count the rows | 0.02 s · 80 MB | 0.01 s · 38 MB | 0.04 s · 43 MB | 0.07 s · 30 MB | 0.07 s · 16 MB |
| Column names and types | 0.02 s · 80 MB | 0.01 s · 37 MB | 0.07 s · 47 MB | 0.07 s · 33 MB | 0.07 s · 14 MB |
| Count rows where a column equals a value | 0.07 s · 160 MB | 0.02 s · 49 MB | 0.05 s · 52 MB | 0.08 s · 41 MB | 0.12 s · 40 MB |
| Distinct values of a column | 0.13 s · 335 MB | 0.04 s · 97 MB | 0.07 s · 54 MB | 0.08 s · 39 MB | 0.12 s · 75 MB |
| Group and sum | 0.17 s · 423 MB | 0.07 s · 201 MB | 0.10 s · 128 MB | 0.12 s · 117 MB | 0.10 s · 147 MB |
| The 10 newest rows, all columns | **out of memory** after 12.8 s | 18.51 s · 3.5 GB | 1.24 s · 678 MB | 1.18 s · 680 MB | 1.43 s · 312 MB |

#### 10 columns × 100M rows (2.6 GB)

| | pandas | Polars | DuckDB | DuckDB CLI | Acrux |
|---|---|---|---|---|---|
| First 10 rows, all columns | 0.73 s · 115 MB | 0.01 s · 40 MB | 0.10 s · 126 MB | 0.13 s · 107 MB | 0.10 s · 13 MB |
| ↳ the whole file first (`read_parquet`) | **out of memory** after 5.5 s | 6.57 s · 7.2 GB |  |  |  |
| A screenful in memory, ready to scroll | **out of memory** after 5.2 s | 6.29 s · 7.2 GB | 5.25 s · 7.3 GB | — | **out of memory** after 5.6 s |
| Count the rows | 0.01 s · 66 MB | 0.00 s · 32 MB | 0.03 s · 32 MB | 0.05 s · 20 MB | 0.07 s · 10 MB |
| Column names and types | 0.01 s · 66 MB | 0.00 s · 30 MB | 0.03 s · 33 MB | 0.05 s · 18 MB | 0.07 s · 8 MB |
| Count rows where a column equals a value | 0.80 s · 270 MB | 0.18 s · 103 MB | 0.17 s · 54 MB | 0.20 s · 41 MB | 0.42 s · 134 MB |
| Distinct values of a column | 2.17 s · 4.6 GB | 0.46 s · 184 MB | 0.18 s · 55 MB | 0.21 s · 41 MB | 0.72 s · 110 MB |
| Group and sum | 4.72 s · 5.1 GB | 1.84 s · 340 MB | 2.08 s · 265 MB | 2.15 s · 249 MB | 2.46 s · 2.3 GB |
| The 10 newest rows, all columns | 6.00 s · 3.7 GB | 5.13 s · 444 MB | 0.97 s · 141 MB | 0.98 s · 323 MB | 3.00 s · 5.9 GB |

### Whole laptop

#### 700 columns × 5M rows (9 GB)

| | pandas | Polars | DuckDB | DuckDB CLI | Acrux |
|---|---|---|---|---|---|
| First 10 rows, all columns | 0.14 s · 988 MB | 0.04 s · 97 MB | 0.50 s · 1.3 GB | 0.43 s · 1.2 GB | 0.15 s · 55 MB |
| ↳ the whole file first (`read_parquet`) | **out of memory** after 2.7 s | **out of memory** after 1.4 s |  |  |  |
| A screenful in memory, ready to scroll | 0.33 s · 2.4 GB | 0.08 s · 1000 MB | 0.29 s · 1.5 GB | — | 0.18 s · 1.6 GB |
| Count the rows | 0.02 s · 80 MB | 0.02 s · 47 MB | 0.05 s · 47 MB | 0.08 s · 33 MB | 0.09 s · 18 MB |
| Column names and types | 0.02 s · 80 MB | 0.01 s · 37 MB | 0.07 s · 50 MB | 0.07 s · 34 MB | 0.07 s · 14 MB |
| Count rows where a column equals a value | 0.05 s · 265 MB | 0.02 s · 75 MB | 0.05 s · 56 MB | 0.08 s · 44 MB | 0.16 s · 61 MB |
| Distinct values of a column | 0.12 s · 424 MB | 0.03 s · 138 MB | 0.07 s · 71 MB | 0.08 s · 55 MB | 0.15 s · 165 MB |
| Group and sum | 0.14 s · 557 MB | 0.03 s · 268 MB | 0.09 s · 178 MB | 0.10 s · 158 MB | 0.07 s · 215 MB |
| The 10 newest rows, all columns | **out of memory** after 6.1 s | **out of memory** after 1.4 s | 0.57 s · 766 MB | 0.49 s · 754 MB | 0.58 s · 391 MB |

#### 10 columns × 100M rows (2.6 GB)

| | pandas | Polars | DuckDB | DuckDB CLI | Acrux |
|---|---|---|---|---|---|
| First 10 rows, all columns | 0.02 s · 118 MB | 0.01 s · 45 MB | 0.11 s · 275 MB | 0.09 s · 253 MB | 0.12 s · 14 MB |
| ↳ the whole file first (`read_parquet`) | **out of memory** after 2.3 s | 2.31 s · 8.0 GB |  |  |  |
| A screenful in memory, ready to scroll | **out of memory** after 2.9 s | 2.03 s · 8.0 GB | 1.41 s · 7.9 GB | — | **out of memory** after 1.4 s |
| Count the rows | 0.01 s · 67 MB | 0.01 s · 40 MB | 0.03 s · 34 MB | 0.07 s · 20 MB | 0.10 s · 12 MB |
| Column names and types | 0.01 s · 67 MB | 0.00 s · 31 MB | 0.04 s · 34 MB | 0.05 s · 20 MB | 0.07 s · 9 MB |
| Count rows where a column equals a value | 0.27 s · 522 MB | 0.04 s · 89 MB | 0.05 s · 82 MB | 0.08 s · 65 MB | 0.19 s · 160 MB |
| Distinct values of a column | 2.10 s · 4.8 GB | 0.10 s · 513 MB | 0.06 s · 89 MB | 0.08 s · 77 MB | 0.29 s · 383 MB |
| Group and sum | 3.36 s · 4.8 GB | 0.21 s · 1.2 GB | 0.17 s · 616 MB | 0.21 s · 619 MB | 0.43 s · 2.4 GB |
| The 10 newest rows, all columns | 1.71 s · 4.0 GB | 0.36 s · 1.3 GB | 0.26 s · 356 MB | 0.24 s · 330 MB | 2.76 s · 5.5 GB |

### Messy files

#### Read with no parameters

Each column graded **right** (the type and every value), **as text** (not
converted, nothing lost) or **wrong** (wrong values, with no error).

| file | pandas | Polars | DuckDB | Acrux |
|---|---|---|---|---|
| `vendas_br.csv` | error: ParserError | error: ComputeError | error: InvalidInputException | 6/6 right |
| `orders_us.csv` | 3/5 right (order_date as text, ship_date as text) | 3/5 right (order_date as text, ship_date as text) | 5/5 right | 4/5 right (ship_date as text) |
| `invoices_us.csv` | 2/3 right (invoice_date as text) | 2/3 right (invoice_date as text) | 2/3 right (**invoice_date wrong, without an error**) | 2/3 right (invoice_date as text) |
| `loja.db` | 4/5 right (quando as text) | error: ComputeError | error: TypeMismatchException | 5/5 right |

- On `vendas_br.csv`, pandas splits at the commas (a "C error: Expected 2
  fields" at the first decimal discount, line 200,019), and Polars and
  DuckDB stop at the encoding ("invalid utf-8 sequence", "CSV Error on Line
  2"). Acrux converts it from Windows-1252, finds the separator and the
  decimal comma, reads the dates as day/month because days past 12 prove it,
  and retries with a larger sample when the discount column turns decimal.
- On the US files, DuckDB chooses one date format for the whole file. In
  `orders_us.csv` the proof in `order_date` makes `ship_date` right too,
  which Acrux leaves as text because it decides column by column. In
  `invoices_us.csv`, with no proof anywhere, DuckDB reads the dates day first
  and swaps US day and month without a word; pandas, Polars and Acrux keep
  them as text.
- On `loja.db`, pandas keeps every value (as Python objects) and leaves the
  times as text; Polars and DuckDB stop at the first text in a numeric
  column. Acrux types each column by what it holds: the mixed ones become
  text with every value, and the times become times, in their zone.

#### `vendas_br.csv` read right (2M rows)

With the parameters someone who knows the file would pass: the encoding,
the separator, the decimal mark, the date format spelled out and the type of
the discount column. Spelled out, not inferred: Polars' `try_parse_dates`
took 26 of its 28 s on this file. Acrux passes nothing, and pays for it:
it finds all of that out, and writes the Parquet file it works from.

| | pandas | Polars | DuckDB | Acrux |
|---|---|---|---|---|
| ordinary PC | 0.66 s · 449 MB | 0.37 s · 637 MB | 0.45 s · 484 MB | 1.91 s · 303 MB |
| whole laptop | 0.67 s · 450 MB | 0.27 s · 651 MB | 0.32 s · 487 MB | 1.71 s · 498 MB |

### What the tall file's first screen needs

The desktop app reads every column of the screen whole, each in a query of
its own, all at once. On the tall file that ran out of memory at 8 GB, so
the memory cap was raised 2 GB at a time, three runs at each, until a tool
held it in all three (ordinary PC). Near what a tool needs, it fits in some
runs and not in others:

| runs that held it, of 3 | 8 GB | 10 GB | 12 GB | 14 GB |
|---|---|---|---|---|
| Acrux, all at once (today) | 0/3 | 1/3 | **3/3** · 10.0 GB, 5.6 s | |
| Acrux, one column at a time | 1/3 | **3/3** · 8.0 GB, 7.2 s | | |
| Polars | **3/3** · 7.2 GB, 6.4 s | | | |
| DuckDB | **3/3** · 7.3 GB, 5.3 s | | | |
| pandas | 0/3 | 0/3 | 0/3 | 0/3 |

The 10 columns take ~7.2 GB in any tool. What Acrux adds on top is the
working memory of the 10 reads in flight: each costs about its column's
size while it loads. Loading k columns at a time, under a 13 GB cap so none
fails ("all" is the 10 columns of the tall file and the 23 the app loads of
the wide one):

| columns loading at once | 1 | 2 | 3 | 4 | 6 | all (today) |
|---|---|---|---|---|---|---|
| tall, ordinary PC | 7.33 s · 8.1 GB | 6.73 s · 8.3 GB | 6.59 s · 8.8 GB | 6.34 s · 9.3 GB | 6.46 s · 9.7 GB | 5.93 s · 11.4 GB |
| tall, whole laptop | 2.87 s · 7.9 GB | 2.31 s · 8.3 GB | 2.04 s · 8.3 GB | 1.72 s · 9.4 GB | 1.60 s · 9.8 GB | 1.30 s · 12.0 GB |
| wide, ordinary PC | 0.66 s · 0.9 GB | 0.61 s · 0.9 GB | 0.60 s · 0.9 GB | 0.58 s · 0.9 GB | 0.61 s · 1.0 GB | 0.60 s · 0.9 GB |
| wide, whole laptop | 0.51 s · 1.0 GB | 0.38 s · 1.0 GB | 0.31 s · 1.0 GB | 0.28 s · 1.0 GB | 0.26 s · 1.0 GB | 0.20 s · 1.8 GB |

On the wide file, where a column is ~40 MB, loading them all at once costs
little and is the fastest. On the tall one, where a column is ~0.8 GB, each
read in flight adds almost 1 GB. So the limit worth having is on the bytes
in flight, not on the number of columns: the wide file would keep loading
dozens at once, and the tall one would load one or two, from 10–12 GB down
to ~8 GB, the columns and one more in flight. That's the next change to the
column cache. It still leaves the tall file at the edge of 8 GB, ~0.8 GB
above Polars and DuckDB, and a PC with less free memory than the columns
themselves needs more: a grid that loads ranges of rows.

The tables come from `bench/results/screen-memory-2026-11-12T0221Z.json`
(the caps) and `screen-memory-2026-11-12T0216Z.json` (k at a time, whose
single run per cap the first one replaced).

## Where Acrux loses

- **Tall files.** Its first screen, the 10 newest rows (5.9 GB where DuckDB
  takes 141 MB) and grouping (2.3 GB where Polars takes 340 MB, a newer
  release of the same engine) all cost more memory than they should.
- **Reading a CSV whose quirks you know.** pandas, Polars and DuckDB with the
  right parameters read `vendas_br.csv` 3 to 7 times faster.
- **Quick questions in an open session.** A library that is loaded answers
  in hundredths of a second; `acrux` starts a process each time, and
  starting .NET is most of its 0.07 s.
- **The 10 newest rows on the wide file:** DuckDB, slightly.

## Repeat it

[`bench/README.md`](../bench/README.md) has the commands. The raw results,
with every run, are in [`bench/results/`](../bench/results), and the tables
above come from them through `bench/summary.py` and `bench/screen_memory.py`.
