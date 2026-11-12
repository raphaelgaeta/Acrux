# The README's animations

Every terminal animation in the README is drawn from a real run. A capture
script runs the commands and records their output and wall time; then
`terminal_gif.py` draws the GIF from that recording. Only the typing is
replayed, and each output appears after its command's real run time.

| GIF | recorded by | recording |
|---|---|---|
| `assets/cli.gif` | `capture_cli.py`, on the 9 GB test file (not in the repository) | `cli.json` |
| `assets/messy_csv.gif` | `make_messy_csv.py`, then `capture_analyst.py` | `analyst.json` |
| `assets/open_grid.gif`, `assets/script_terminal.gif` | screen recordings on Windows, re-timed by `edit_gif.py`, which marks every sped-up stretch | — |

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
