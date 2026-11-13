# The README's animations

Every terminal animation in the README is drawn from a real run. A capture
script runs the commands and records their output and wall time; then
`terminal_gif.py` draws the GIF from that recording. Only the typing is
replayed, and each output appears after its command's real run time.

| GIF | recorded by | recording |
|---|---|---|
| `assets/cli.gif` | `capture_cli.py`, on the 9 GB test file (not in the repository) | `cli.json` |
| `assets/messy_csv.gif` | `make_messy_csv.py`, then `capture_analyst.py` | `analyst.json` |
| `assets/open_grid.gif` | a screen recording in a Windows 11 virtual machine, re-timed by `edit_gif.py` ([below](#opening-and-filtering-the-wide-file)) | — |
| `assets/script_terminal.gif` | a screen recording on Windows, re-timed by `edit_gif.py`, which marks every sped-up stretch | — |

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

## Opening and filtering the wide file

`open_grid.gif` is the desktop app opening the 9 GB test file (700 columns ×
5 million rows) in a Windows 11 virtual machine, at commit `b8e1d23`, and
filtering one header. The recording is not in the repository. ffmpeg takes
the window's top-left corner, from the window opening to the filter's
result, and keeps 10 frames a second, dropping a frame only when it repeats
the one before (`mpdecimate`), so every change stays at its real time:

```
ffmpeg -ss 3.7 -to 45.15 -i recording.mp4 -an -vf "crop=1200:712:0:0,scale=900:534:flags=lanczos,fps=10,mpdecimate,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -fps_mode vfr open_grid_src.gif
python3 edit_gif.py open_grid_src.gif ../../assets/open_grid.gif "1-34:2,35-49:1,50-64:cut,65-73:2,74-74:1,75-102:1.5,103-107:1,hold:3"
```

- **At real speed, the app's own waits:** reading the file and setting up
  the rows (8.3 s, counted in the status bar), reading the column's values
  and applying the filter (2.7 s, of which the grid took 1.7).
- **Sped up and marked:** choosing the file (2×), going to the header (2×)
  and checking the values (1.5×).
- **Cut and marked:** 16 s in the middle, while the Windows widgets panel
  was open over the grid by accident. The app was idle; only the memory cap
  in the status bar moved, from 2.1 to 2.0 GB.
- **Held:** the last frame stays 3 s more, to read the result before the
  loop.
