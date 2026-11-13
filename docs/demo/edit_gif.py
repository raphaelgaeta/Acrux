"""Re-times a screen-capture GIF: speeds up the human parts (clicking,
moving the mouse), never the app's own waiting, and marks every sped-up
stretch with a small "N×" badge.
Usage: python3 edit_gif.py <in.gif> <out.gif> <plan>
plan: comma list of first-last:speed, by frame number, e.g. "0-6:1,7-26:1.5".
first-last:cut drops those frames and marks the next second with "N s cut";
hold:S keeps the last frame S seconds more, for reading before the loop."""
import os, subprocess, sys, tempfile
from PIL import Image, ImageDraw, ImageFont

src, dst, plan = sys.argv[1], sys.argv[2], sys.argv[3]
MIN_MS = 40   # browsers stretch GIF delays under 20 ms to 100 ms; keep a margin

im = Image.open(src)
frames, delays = [], []
try:
    while True:
        frames.append(im.convert("RGB").copy())
        delays.append(im.info.get("duration", 100))
        im.seek(im.tell() + 1)
except EOFError:
    pass

font = ImageFont.truetype(subprocess.run(["fc-match", "-f", "%{file}", "DejaVu Sans:bold"],
                                         capture_output=True, text=True).stdout, 15)

def badge(frame, text):
    f = frame.copy()
    d = ImageDraw.Draw(f, "RGBA")
    w = d.textlength(text, font=font)
    x1, y1 = f.width - 12, f.height - 12
    d.rounded_rectangle((x1 - w - 16, y1 - 26, x1, y1), radius=6, fill=(30, 30, 30, 190))
    d.text((x1 - w - 8, y1 - 22), text, font=font, fill=(255, 255, 255, 255))
    return f

CUT_MARK_MS = 1000
out = []   # (frame, ms)
cut_note, cut_left = None, 0.0
for part in plan.split(","):
    rng, speed = part.split(":")
    if rng == "hold":
        frame, ms = out[-1]
        out[-1] = (frame, ms + round(float(speed) * 1000))
        continue
    first, last = map(int, rng.split("-"))
    if speed == "cut":
        cut_note, cut_left = f"{sum(delays[first:last + 1]) / 1000:.0f} s cut", CUT_MARK_MS
        continue
    speed = float(speed)
    acc = 0.0
    for i in range(first, last + 1):
        acc += delays[i] / speed
        if acc >= MIN_MS or i == last:
            labels = ([cut_note] if cut_left > 0 else []) + ([f"{speed:g}×"] if speed != 1 else [])
            out.append((badge(frames[i], " · ".join(labels)) if labels else frames[i], round(acc)))
            cut_left -= acc
            acc = 0.0

total = sum(ms for _, ms in out) / 1000
with tempfile.TemporaryDirectory() as tmp:
    lines = []
    for k, (f, ms) in enumerate(out):
        path = os.path.join(tmp, f"{k:04d}.png")
        f.save(path)
        lines += [f"file '{path}'", f"duration {ms / 1000:.3f}"]
    lines.append(f"file '{os.path.join(tmp, f'{len(out) - 1:04d}.png')}'")   # concat needs the last file twice
    listing = os.path.join(tmp, "list.txt")
    open(listing, "w").write("\n".join(lines) + "\n")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listing,
                    "-vf", "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                    "-fps_mode", "vfr", dst], check=True)
print(f"{len(frames)} → {len(out)} frames, {sum(delays) / 1000:.1f} s → {total:.1f} s, "
      f"{os.path.getsize(src) / 1e6:.1f} MB → {os.path.getsize(dst) / 1e6:.1f} MB")
