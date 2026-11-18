# The README's animations

Every terminal animation in the README is drawn from a real run. A capture
script runs the commands and records their output and wall time; then
`terminal_gif.py` draws the GIF from that recording. Only the typing is
replayed, and each output appears after its command's real run time.

| GIF | recorded by | recording |
|---|---|---|
| `assets/cli.gif` | `capture_cli.py`, on the 9 GB test file (not in the repository) | `cli.json` |
| `assets/messy_csv.gif` | `make_messy_csv.py`, then `capture_analyst.py` | `analyst.json` |
| `assets/open_grid.gif` | a screen recording in a Windows 11 virtual machine, re-timed by `edit_gif.py` ([below](#opening-the-wide-file)) | — |
| `assets/header_filter.gif` | the same kind of recording, re-timed by `edit_gif.py` ([below](#a-header-filter-and-the-c-terminal)) | — |
| `assets/script_terminal.gif` | the same recording as `header_filter.gif`, a later stretch ([below](#a-header-filter-and-the-c-terminal)) | — |

Drawing needs Pillow, ffmpeg and, for the same look, the JetBrains Mono font.

## The messy CSV, pandas next to Acrux

```
python3 make_messy_csv.py /tmp/demo
python3 capture_analyst.py /tmp/demo analyst.json    # needs pandas and acrux on the PATH
python3 terminal_gif.py analyst.json ../../assets/messy_csv.gif
```

What is real and what is not:

- **The file is synthetic** (fixed seed), written the way Excel saves a sheet
  as CSV on a Portuguese-language Windows: Windows-1252, `;` between fields,
  decimal commas, day/month dates. The first date, `03/02/2026`, reads both
  ways.
- **The pandas lines run as the Python REPL runs them**, in one session, and
  their output is pandas' own. A traceback keeps its first line and the
  error; the frames in between are cut to `…`. The full traceback is in
  `analyst.json`, under `raw`.
- **The times are real**, from a second run of each side, with the disk
  cache warm. The pandas session starts with the REPL already open. Each
  `acrux` command is a new process, so its time includes starting .NET and
  converting the CSV.
- **Both sides follow the same pace:** an output stays on screen 1.2 s plus
  0.2 s a line (3.5 s at most) before the next command, and a command that
  starts like an earlier one comes back from the history (↑) and is
  finished by hand.

The fair reading: pandas reads this file once you find the encoding, the
separator, the decimal mark and `dayfirst`. Acrux works them out from the
data. It isn't always the one that converts more, though. If every day in the
file were 12 or less, Acrux would keep the dates as text, because the data
wouldn't prove the order, while `dayfirst=True` would convert them.

## Opening the wide file

`open_grid.gif` is the desktop app opening the 9 GB test file (700 columns ×
5 million rows) in a Windows 11 virtual machine, at commit `8de5db3` (v0.3.0),
recorded on 2026-11-17. The recording is not in the repository. ffmpeg takes
the window's top-left corner, from the empty window to the first screen of
data, and keeps 10 frames a second, dropping a frame only when it repeats the
one before (`mpdecimate`), so every change stays at its real time:

```
ffmpeg -ss 0.7 -to 7.4 -i recording.mp4 -an -vf "crop=1200:712:0:0,scale=900:534:flags=lanczos,fps=10,mpdecimate,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -fps_mode vfr open_grid_src.gif
python3 edit_gif.py open_grid_src.gif ../../assets/open_grid.gif "0-23:2,24-30:1,hold:3"
```

- **At real speed, the app's own wait:** from the click on Open to the whole
  first screen, 0.9 s. The status bar shows the 5,000,000 rows × 700 columns
  0.2 s after the click, and the 39 columns on the screen arrive in the next
  0.7 s (1.4 GB in memory).
- **Sped up and marked:** choosing the file in the dialog (2×).
- **Held:** the last frame stays 3 s more, to read it before the loop.

The GIF it replaced came from the Windows-only app before the move to
Avalonia (commit `b8e1d23`, 2026-11-12), in the same virtual machine and on
the same file: reading the file and setting up the rows took 8.3 s there,
counted in the status bar, before a header filter took 2.7 s more.

## A header filter and the C# terminal

`header_filter.gif` and `script_terminal.gif` come from one recording of the
desktop app window, at its first size (1280×800, not maximized, so the
terminal fits under the grid), in the same virtual machine, at commit
`8de5db3` (v0.3.0), on 2026-11-17, with the 9 GB test file open. Both crop
the window's left side and keep the same 10 frames a second without repeats.

```
ffmpeg -ss 17.6 -to 28.6 -i recording.mp4 -an -vf "crop=1060:650:0:0,scale=900:-2:flags=lanczos,fps=10,mpdecimate,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -fps_mode vfr filter_src.gif
python3 edit_gif.py filter_src.gif ../../assets/header_filter.gif "0-24:2,25-27:1,hold:3"

ffmpeg -ss 57 -to 103.5 -i recording.mp4 -an -vf "crop=1060:812:0:0,scale=900:-2:flags=lanczos,fps=10,mpdecimate,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -fps_mode vfr term_src.gif
python3 edit_gif.py term_src.gif ../../assets/script_terminal.gif "0-12:3,13-19:1,20-40:3,41-45:1,46-63:4,64-71:3,72-75:1,76-86:3,87-91:1,92-100:3,101-106:1,hold:3"
```

`header_filter.gif`:

- **At real speed:** from OK to the filtered grid. The status bar says
  2,545,975 of 5,000,000 rows, the query in 200 ms and the grid in 0 ms.
- **Sped up and marked:** opening the filter, "Uncheck all" and checking
  `false` (2×). "(Blanks)" stays checked, as in Excel, so the count includes
  the column's 96,700 nulls.

`script_terminal.gif`, three steps chained on the whole file (the header
filter was cleared before them), then "Undo step" and "Restore file":

- **At real speed:** every wait from a click to its result. The terminal
  prints each time: step 1, `df.Filter(Col("c004_f32") > 90)`, 495,275 rows
  × 700 columns in 1,890 ms; step 2, a `GroupBy` with `Mean` and `Len`, 21
  rows × 3 columns in 231 ms; step 3, `df.Sort("rows", descending: true)`,
  in 283 ms; the undo back to step 2, in 162 ms; and the file back.
- **Sped up and marked:** pasting each step, reading the results and
  going to the buttons (3×, and 4× for the 11 s before step 3).
- **Held:** the last frame stays 3 s more.

The terminal GIF it replaced was a Windows-only app recording, with the
earlier name `lf` for the plan.
