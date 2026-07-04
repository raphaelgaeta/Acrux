# PolarsGridViewer

A Windows desktop viewer for **Parquet and CSV files that spreadsheet tools can't open** —
hundreds of columns, millions of rows — powered by [Polars.NET](https://www.nuget.org/packages/Polars.NET)
and a zero-materialization virtual grid.

<!-- TODO: screenshot / GIF of the grid + filter + terminal here -->

## Why

Analytical exports keep outgrowing Excel: too many columns to render, too many rows to
load, CSV dialects that break parsers. This project started as a work necessity and
became an exercise in how far a plain WinForms `DataGridView` can go when **the data
never leaves the Arrow buffers** and every interaction is delegated to a lazy query
engine. The reference workload is a 700-column × 5-million-row parquet (8.4 GB):
it opens in seconds and filters in tens of milliseconds.

## Features

- **Parquet and CSV input.** CSV is converted once to a temporary parquet
  (streaming, RAM-bounded) and the app operates on parquet from then on. The
  converter survives real-world files: separator sniffing (`;` / `,` / tab),
  decimal-comma detection from the data, Windows-1252 ("ANSI" Excel) and UTF-16
  transcoding, and schema inference that retries with a larger sample — flipping
  the decimal convention if the offending value disproves it — when a column
  changes type past the sample.
- **Column picker on open** — projection pushdown means only selected columns are
  ever read from disk.
- **Zero materialization.** Cells are decoded on demand from the Arrow
  `RecordBatch` in the `CellValueNeeded` callback. No `DataTable`, no `object[]`
  copies, no paging.
- **Excel-style header filters.** Click a header for the distinct-values dropdown
  (blanks included, capped at 10,000). Filters are recomputed by lazily
  re-scanning the file — tens of milliseconds on 5M rows — and compose across
  columns with Excel semantics.
- **C# scripting terminal.** A Roslyn-powered REPL where `lf` is the current
  `LazyFrame` and the full Polars expression API is available:

  ```csharp
  lf.Filter(Col("city") == "São Paulo")
  lf.GroupBy("product").Agg(Col("value").Sum().Alias("total"))
  ```

  Each command **chains on the previous result** (Power-Query-style applied
  steps), but nothing is collected between steps: the chain is replayed as a
  single lazy plan, so the optimizer sees the whole pipeline. One click undoes a
  step or restores the original file. An optional 1M-row cap keeps accidental
  `lf` collects from eating your RAM.

## Building

```
dotnet build PolarsGridViewer.csproj
dotnet run --project PolarsGridViewer.csproj
```

Requires the .NET 10 SDK (Windows). Single-file self-contained publishing works:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Two publish rules are enforced by the project file — don't fight them:
`PublishTrimmed` is unsupported (WinForms), and `IncludeAllContentForSelfExtract`
is required for the scripting terminal (Roslyn resolves references via
`Assembly.Location`, which is empty inside a pure single-file bundle).

## Architecture

| File | Role |
|---|---|
| `MainForm.cs` | Virtual grid, file loading, filter orchestration |
| `DataFrameProvider.cs` | Parquet reading and CSV→parquet conversion |
| `ColumnSelectorForm.cs` | Column picker shown on open |
| `FilterEngine.cs` | Excel-style filters as Polars lazy queries |
| `FilterPopupForm.cs` | Distinct-values popup |
| `PolarsTableAdapter.cs` | O(1) per-cell reads from Arrow arrays |
| `ScriptHost.cs` | Roslyn evaluation of chained Polars scripts |
| `ScriptTerminalPanel.cs` | Terminal UI (bottom dock) |

The deeper design docs — including the non-negotiable principles (zero
materialization, index indirection, lazy rescans) and a catalog of Polars.NET
pitfalls discovered through isolated probing — live in [CLAUDE.md](CLAUDE.md).
This project is developed with [Claude Code](https://claude.com/claude-code),
and that file doubles as the agent's working context: every API quirk listed
there (native crashes, silent GPU fallback, schema-handle lifetimes, encoding
traps) was validated empirically before being relied on.

## Status

Personal project, maintained as time allows — issues and PRs are welcome, but
there's no SLA. Windows-only by design (WinForms).
