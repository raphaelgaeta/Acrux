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
before   5.000.000 rows ×  700 columns:  13,45 s, a message waited up to  13,45 s (one RowCount)
after    5.000.000 rows ×  700 columns:   7,10 s, a message waited up to   0,64 s (15 blocks of ~500 ms)
```

"A message waited" is how long the window took to answer: a thread posts a
message every 10 ms and times its wait, as a click would wait. A WinForms
timer can't measure this, since Windows only hands out `WM_TIMER` when the
queue is empty.

Measured on 2026-11-12 under Wine on a laptop (i9, Fedora): the line above.
Block size against total time, from the earlier probe: 0.1 s blocks cost
~40% over a single `RowCount` with the columns hidden, 0.25 s ~30%, 0.5 s
~4%.
