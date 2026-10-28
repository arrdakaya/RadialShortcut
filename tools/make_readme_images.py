"""Builds the README images from the app's own drawing of its rings and drop zones.

Usage: python tools/make_readme_images.py [path to RadialShortcut.exe]
Runs `RadialShortcut.exe --snapshot`, then lays the shots out as docs/hero.png and docs/modes.png.
"""
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
EXE = ROOT / "src/RadialShortcut.App/bin/Release/net10.0-windows10.0.19041.0/RadialShortcut.exe"
FONTS = Path("C:/Windows/Fonts")
BG_TOP, BG_BOT = (13, 16, 22), (23, 28, 38)
ACCENT = (61, 139, 255)


def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size)


def background(w, h):
    img = Image.new("RGBA", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / max(h - 1, 1)
        d.line([(0, y), (w, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOT)) + (255,))
    for x in range(0, w, 24):  # faint dot grid
        for y in range(0, h, 24):
            d.point((x, y), fill=(255, 255, 255, 14))
    return img


def glow(img, center, radius, alpha=90):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    cx, cy = center
    ImageDraw.Draw(layer).ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=ACCENT + (alpha,))
    img.alpha_composite(layer.filter(ImageFilter.GaussianBlur(radius / 2.5)))


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, img.width - 1, img.height - 1], radius, fill=255)
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.paste(img, mask=mask)
    return out


def fit(img, height):
    return img.resize((round(img.width * height / img.height), height), Image.LANCZOS)


def hero(ring):
    w, h = 1600, 700
    img = background(w, h)
    ring = fit(ring, 620)
    center = (1130, h // 2)
    glow(img, center, 300)
    img.alpha_composite(ring, (center[0] - ring.width // 2, center[1] - ring.height // 2))
    d = ImageDraw.Draw(img)
    d.text((100, 230), "RadialShortcut", font=font("segoeuib.ttf", 82), fill="white")
    d.text((104, 345), "Right-click, hold, flick. Done.", font=font("segoeui.ttf", 34), fill=(170, 178, 189))
    d.text((104, 400), "A radial shortcut menu for Windows", font=font("segoeui.ttf", 24), fill=(118, 128, 140))
    d.text((104, 434), "Screen and file tools, all on your machine", font=font("segoeui.ttf", 24), fill=(118, 128, 140))
    return rounded(img, 24)


def modes(main, files, drops):
    w, h = 1600, 640
    img = background(w, h)
    panels = [(fit(main, 420), "Screen ring", "Right-click + drag"),
              (fit(files, 420), "File ring", "With files selected in Explorer"),
              (fit(drops, 330), "Drop zones", "Shift while dragging a file")]
    d = ImageDraw.Draw(img)
    col = w // 3
    for i, (shot, title, sub) in enumerate(panels):
        cx = col * i + col // 2
        cy = 275
        glow(img, (cx, cy), 170, 60)
        img.alpha_composite(shot, (cx - shot.width // 2, cy - shot.height // 2))
        for text, f, fill, y in ((title, font("segoeuib.ttf", 30), "white", 520), (sub, font("segoeui.ttf", 22), (140, 150, 162), 562)):
            tw = d.textlength(text, font=f)
            d.text((cx - tw / 2, y), text, font=f, fill=fill)
    return rounded(img, 24)


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else EXE
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([str(exe), "--snapshot", tmp], check=True, timeout=120)
        shots = {n: Image.open(Path(tmp) / f"{n}.png").convert("RGBA") for n in ("ring-main", "ring-files", "drop-zones")}
    hero(shots["ring-main"]).save(DOCS / "hero.png", optimize=True)
    modes(shots["ring-main"], shots["ring-files"], shots["drop-zones"]).save(DOCS / "modes.png", optimize=True)


if __name__ == "__main__":
    main()
