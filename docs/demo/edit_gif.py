"""Re-times a screen-capture GIF: speeds up the human parts (clicking,
moving the mouse), never the app's own waiting, and marks every sped-up
stretch with a small "N×" badge.
Usage: python3 edit_gif.py <in.gif> <out.gif> <plan>
plan: comma list of first-last:speed, by frame number, e.g. "0-6:1,7-26:1.5"."""
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

def badge(frame, speed):
    f = frame.copy()
    d = ImageDraw.Draw(f, "RGBA")
    text = f"{speed:g}×"
    w = d.textlength(text, font=font)
    x1, y1 = f.width - 12, f.height - 12
    d.rounded_rectangle((x1 - w - 16, y1 - 26, x1, y1), radius=6, fill=(30, 30, 30, 190))
    d.text((x1 - w - 8, y1 - 22), text, font=font, fill=(255, 255, 255, 255))
    return f

out = []   # (frame, ms)
for part in plan.split(","):
    rng, speed = part.split(":")
    first, last = map(int, rng.split("-"))
    speed = float(speed)
    acc = 0.0
    for i in range(first, last + 1):
        acc += delays[i] / speed
        if acc >= MIN_MS or i == last:
            out.append((badge(frames[i], speed) if speed != 1 else frames[i], round(acc)))
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
