"""Renders the app icon (white ring glyph on an accent tile, top segment selected) to PNG + multi-size ICO.

Colors mirror RingWindow: accent #3D8BFF, accent2 = accent hue-shifted +45deg, dark slice fill.
Usage: python tools/make_icon.py
"""
import colorsys
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "src" / "RadialShortcut.App" / "Assets"
SS = 4  # supersampling factor


def hex_rgb(h):
    return np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)], float)


ACCENT = hex_rgb("#3D8BFF")
_h, _l, _s = colorsys.rgb_to_hls(*(ACCENT / 255))
ACCENT2 = np.array(colorsys.hls_to_rgb((_h + 45 / 360) % 1, _l, _s)) * 255


def render(size):
    n = size * SS
    y, x = np.mgrid[0:n, 0:n].astype(float)
    c = n / 2
    dx, dy = (x + 0.5 - c) / c, (y + 0.5 - c) / c  # unit space, -1..1
    r = np.hypot(dx, dy)
    ang = np.degrees(np.arctan2(dx, -dy)) % 360  # 0 = up, clockwise

    small = size <= 24
    rgb = np.zeros((n, n, 3))
    a = np.zeros((n, n))

    # Rounded-square tile with a diagonal accent gradient; small sizes fill the whole canvas.
    half_w = 0.94 if not small else 1.0
    rad = 0.42 if not small else 0.36
    qx, qy = np.abs(dx) - (half_w - rad), np.abs(dy) - (half_w - rad)
    tile = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) <= rad
    k = np.clip((dx + dy + 2) / 4, 0, 1)[..., None]
    rgb[tile] = (ACCENT * (1 - k) + ACCENT2 * k)[tile]
    a[tile] = 1

    # White ring: four thick segments, the top one selected (solid), the rest translucent.
    r_out, r_in = (0.68, 0.40) if not small else (0.80, 0.44)
    gap = 0.11 if not small else 0.16
    local = (ang + 45) % 90 - 45
    edge = np.radians(45 - np.abs(local))
    seg = tile & (r >= r_in) & (r <= r_out) & (r * np.sin(edge) > gap / 2)
    top = seg & (np.abs(((ang + 180) % 360) - 180) < 45)
    mix = np.where(top, 1.0, 0.42)[..., None]
    rgb[seg] = (rgb + (255 - rgb) * mix)[seg]

    # Center dot.
    dot = r <= (0.20 if not small else 0.24)
    rgb[dot] = 255

    # Premultiplied downsample for clean edges.
    pre = np.dstack([rgb * a[..., None], a])
    pre = pre.reshape(size, SS, size, SS, 4).mean(axis=(1, 3))
    alpha = pre[..., 3:]
    col = np.where(alpha > 0, pre[..., :3] / np.maximum(alpha, 1e-6), 0)
    img = np.dstack([col, alpha * 255]).round().clip(0, 255).astype(np.uint8)
    return Image.fromarray(img, "RGBA")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    render(1024).save(OUT / "icon.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(s) for s in sizes]
    frames[-1].save(OUT / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    preview = Image.new("RGBA", (sum(sizes) + 10 * len(sizes) + 10, 276), (32, 32, 32, 255))
    xo = 10
    for f in frames:
        preview.alpha_composite(f, (xo, 10))
        xo += f.width + 10
    preview.save(OUT.parent.parent.parent / "tools" / "icon_preview.png")


if __name__ == "__main__":
    main()
