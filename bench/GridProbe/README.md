# GridProbe

Times the desktop app's grid setup, without a file: a `DataGridView` with
the app's properties gets its columns and rows twice, the way the app did
before `GridRows` (one `RowCount`, columns visible) and the way it does now
(`src/Acrux.WinForms/GridRows.cs`, compiled in, so the probe measures the
app's own code).

Why it exists: `RowCount = n` in WinForms costs time per row **and per
column**, because each new row walks the visible columns. With 5 million rows
and 700 columns, a 2017 notebook (i7-7500U) showed a white window for 47 s
before the first screen, and the benchmark, which goes through the core
without the grid, never saw it.

```
dotnet publish bench/GridProbe/GridProbe.csproj -c Release -r win-x64 --self-contained true -o bench/GridProbe/bin/publish
bench\GridProbe\bin\publish\GridProbe.exe [rows] [columns] [block ms]     # 5000000 700 500
```

It runs on Windows, or on Linux under Wine (the managed code is the same, so
the times compare). Each run appends two lines to `gridprobe.log`, next to
the executable:

```
before   5.000.000 rows ×  700 columns:  13,17 s, a message waited up to  13,16 s (one RowCount); then a click   3,4 ms, selecting 300 rows    268 ms, Rows.Clear()    57 ms
after    5.000.000 rows ×  700 columns:   7,09 s, a message waited up to   0,55 s (15 blocks of ~500 ms); then a click   4,3 ms, selecting 300 rows    312 ms, Rows.Clear()    49 ms
```

"A message waited" is how long the window took to answer: a thread posts a
message every 10 ms and times its wait, as a click would wait. A WinForms
timer can't measure this, since Windows only hands out `WM_TIMER` when the
queue is empty.

The click, the selection and the `Rows.Clear()` after each setup ask the
grid for rows by index, which a filter and a selection do in the app. They
caught the first version of `GridRows`, whose blocks each had their own
shared row: 300 rows took 1.1 to 2.9 s to select, and `Rows.Clear()` 135 ms
(1.7 s on the Windows 11 VM). Now the blocks share one row object.

Measured on 2026-11-15 under Wine on a laptop (i9, Fedora): the lines above,
the last of three runs (selecting 300 rows took 220–268 ms before and
312–432 ms after).
Block size against total time, from the earlier probe: 0.1 s blocks cost
~40% over a single `RowCount` with the columns hidden, 0.25 s ~30%, 0.5 s
~4%.
