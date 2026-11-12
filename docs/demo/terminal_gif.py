"""Draws a GIF of one or more terminal sessions, side by side on one clock,
from real runs recorded by a capture script (capture_cli.py,
capture_analyst.py). Each command is typed at a human pace, and its output
appears after the command's real wall time.
Usage: python3 terminal_gif.py <capture.json> <out.gif>

The capture: {"panes": [{"title", "prompt", "scenes": [[{"cmd", "seconds",
"out", "error"?}]]}]} and, optionally, "width" and "height" of each pane
(px), "read_ms": [base, per output line, max] for how long an output stays
before the next command, and "history": true to bring back with ↑ a command
that starts like an earlier one. Needs Pillow, ffmpeg and, for the look of
the README, the JetBrains Mono font."""
import json, os, random, subprocess, sys, tempfile
from PIL import Image, ImageDraw, ImageFont

capture, dst = sys.argv[1], sys.argv[2]
spec = json.load(open(capture, encoding="utf-8"))
if isinstance(spec, list):   # the first captures were one pane's scenes
    spec = {"panes": [{"title": "acrux — bash", "prompt": "$ ", "scenes": spec}]}
random.seed(7)   # the typing jitter is the same on every render

SIZE = 16
def font_file(style):
    return subprocess.run(["fc-match", "-f", "%{file}", f"JetBrains Mono:{style}"], capture_output=True, text=True).stdout
font, bold = ImageFont.truetype(font_file("regular"), SIZE), ImageFont.truetype(font_file("bold"), SIZE)
CW, LH = font.getlength("M"), round(SIZE * 1.4)
PAD, BAR, GAP = 18, 34, 6
W, H = spec.get("width", 900), spec.get("height", 534)   # 900×534: the desktop GIF's size
COLS, ROWS = int((W - 2 * PAD) // CW), int((H - BAR - 2 * PAD) // LH)
READ_BASE, READ_LINE, READ_MAX = spec.get("read_ms", [700, 150, 2400])
HISTORY = spec.get("history", False)
BG, FG, DIM, GREEN, RED, BARBG = (30, 30, 46), (240, 240, 245), (186, 194, 222), (166, 227, 161), (243, 139, 168), (24, 24, 37)
DOTS = [(243, 139, 168), (249, 226, 175), (166, 227, 161)]
PLAIN = ["-liga", "-calt"]   # no ligatures: "==" must look like the two characters the program prints
TICK = 40   # every frame boundary on a 40 ms grid: browsers stretch GIF delays under 20 ms to 100 ms

def draw(pane, lines, cursor):
    """lines: [(text, kind)] with kind in prompt/out/err; cursor: (line, column in its text) or None"""
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, W, BAR), fill=BARBG)
    for k, color in enumerate(DOTS):
        d.ellipse((PAD + k * 22, BAR / 2 - 6, PAD + k * 22 + 12, BAR / 2 + 6), fill=color)
    d.text(((W - font.getlength(pane["title"])) / 2, BAR / 2 - SIZE / 2 - 2), pane["title"], font=font, fill=DIM, features=PLAIN)
    prompt, rows, at = pane["prompt"], [], None
    for n, (text, kind) in enumerate(lines):   # a line longer than the pane wraps, as in a terminal
        full = prompt + text if kind == "prompt" else text
        first = len(rows)
        rows += [(full[k:k + COLS], kind, k == 0) for k in range(0, max(len(full), 1), COLS)]
        if cursor and cursor[0] == n:
            col = (len(prompt) if kind == "prompt" else 0) + cursor[1]
            at = (first + col // COLS, col % COLS)
            if at[0] == len(rows):
                rows.append(("", kind, False))
    skip = max(0, len(rows) - ROWS)
    for r, (text, kind, first) in enumerate(rows[skip:]):
        y = BAR + PAD + r * LH
        if kind == "prompt" and first:
            d.text((PAD, y), prompt, font=bold, fill=GREEN, features=PLAIN)
            d.text((PAD + CW * len(prompt), y), text[len(prompt):], font=bold, fill=FG, features=PLAIN)
        else:
            d.text((PAD, y), text, font=bold if kind == "prompt" else font,
                   fill=FG if kind == "prompt" else RED if kind == "err" else DIM, features=PLAIN)
    if at:
        x, y = PAD + CW * at[1], BAR + PAD + (at[0] - skip) * LH
        d.rectangle((x, y + 2, x + CW - 1, y + LH - 3), fill=FG)
    return img

def session(pane):
    """The pane's screen over time: [(ms, lines, cursor)], and the last screen."""
    timeline = []
    def show(lines, cursor, ms):
        timeline.append((ms, tuple(lines), cursor))
    def idle(lines, ms):   # blinking cursor on the prompt line
        on = True
        while ms > 0:
            show(lines, (len(lines) - 1, len(lines[-1][0])) if on else None, min(500, ms))
            ms -= 500; on = not on
    def type_command(lines, cmd, typed):
        start = max((len(os.path.commonprefix([cmd, old])) for old in typed), default=0) if HISTORY else 0
        if start < 10:
            start = 0
        if start:   # ↑ brings the earlier command back, and the rest is typed
            lines[-1] = (cmd[:start], "prompt"); show(lines, (len(lines) - 1, start), 400)
        # two characters a frame: a GIF frame lasts 40 ms at least, one per key is too slow
        for k in list(range(start + 2, len(cmd), 2)) + [len(cmd)]:
            lines[-1] = (cmd[:k], "prompt")
            show(lines, (len(lines) - 1, k), random.uniform(45, 70))
    lines, typed = [("", "prompt")], []
    idle(lines, 600)
    for n, scene in enumerate(pane["scenes"]):
        if n > 0:
            type_command(lines, "clear", []); show(lines, None, 250)
            lines = [("", "prompt")]; idle(lines, 600)
        for step in scene:
            type_command(lines, step["cmd"], typed); typed.append(step["cmd"])
            show(lines, (len(lines) - 1, len(step["cmd"])), 250)   # a beat before Enter
            show(lines + [("", "out")], (len(lines), 0), step["seconds"] * 1000)   # the command's real run time
            kind = "err" if step.get("error") else "out"
            lines += [(text.expandtabs(8), kind) for text in step["out"]]   # tab stops every 8, like a terminal
            lines.append(("", "prompt"))
            idle(lines, min(READ_MAX, READ_BASE + READ_LINE * len(step["out"])))
    return timeline, lines

panes = spec["panes"]
sessions = [session(p) for p in panes]
end = max(sum(ms for ms, _, _ in t) for t, _ in sessions) + 2500   # hold the last screen
for t, last in sessions:   # a pane that finishes first waits, cursor blinking, for the others
    left, on = end - sum(ms for ms, _, _ in t), True
    while left > 0:
        t.append((min(500, left), tuple(last), (len(last) - 1, 0) if on else None)); left -= 500; on = not on

# each pane's screen changes at its own times: a frame per span in which no pane changes
marks = []
for t, _ in sessions:
    clock, m = 0, []
    for ms, _, _ in t:
        clock += ms; m.append(round(clock / TICK) * TICK)
    marks.append(m)
cuts = sorted({c for m in marks for c in m if c > 0})
frames, at, shown, images, prev = [], [0] * len(panes), [None] * len(panes), [None] * len(panes), 0
canvas_w = W * len(panes) + GAP * (len(panes) - 1)
for cut in cuts:
    for p in range(len(panes)):
        while marks[p][at[p]] <= prev:   # skip the screens snapped away by the grid
            at[p] += 1
        if shown[p] != at[p]:
            _, lines, cursor = sessions[p][0][at[p]]
            images[p], shown[p] = draw(panes[p], list(lines), cursor), at[p]
    img = Image.new("RGB", (canvas_w, H), BARBG)
    for p, pane_img in enumerate(images):
        img.paste(pane_img, (p * (W + GAP), 0))
    frames.append((img, cut - prev))
    prev = cut

with tempfile.TemporaryDirectory() as tmp:
    listing = []
    for k, (img, ms) in enumerate(frames):
        path = os.path.join(tmp, f"{k:05d}.png"); img.save(path)
        listing += [f"file '{path}'", f"duration {ms / 1000:.3f}"]
    listing.append(f"file '{os.path.join(tmp, f'{len(frames) - 1:05d}.png')}'")
    open(os.path.join(tmp, "list.txt"), "w").write("\n".join(listing) + "\n")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", os.path.join(tmp, "list.txt"),
                    "-vf", "split[a][b];[a]palettegen=max_colors=64:stats_mode=diff[p];[b][p]paletteuse=dither=none:diff_mode=rectangle",
                    "-fps_mode", "vfr", dst], check=True)
print(f"{canvas_w}×{H}, {len(frames)} frames, {sum(ms for _, ms in frames) / 1000:.1f} s, {os.path.getsize(dst) / 1e6:.2f} MB")
