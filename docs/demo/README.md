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
