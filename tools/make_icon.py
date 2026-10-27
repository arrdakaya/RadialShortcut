"""Renders the app icon (the ring itself, top slice selected) to PNG + multi-size ICO.

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
DARK_IN, DARK_OUT = hex_rgb("#3A3E47"), hex_rgb("#5A5F6A")
CENTER_TOP, CENTER_BOT = hex_rgb("#33363E"), hex_rgb("#1C1E23")


def render(size):
    n = size * SS
    y, x = np.mgrid[0:n, 0:n].astype(float)
    c = n / 2
    dx, dy = (x + 0.5 - c) / c, (y + 0.5 - c) / c  # unit space, -1..1
    r = np.hypot(dx, dy)
    ang = np.degrees(np.arctan2(dx, -dy)) % 360  # 0 = up, clockwise

    small = size <= 24
    slices = 8
    r_out = 0.90 if not small else 0.98
    r_in = 0.42 if not small else 0.38
    r_ctr = 0.35 if not small else 0.32
    gap = (0.045 if not small else 0.09)  # gap width in unit space

    rgb = np.zeros((n, n, 3))
    a = np.zeros((n, n))

    # Slices: a pixel is in a gap if its perpendicular distance to a divider ray is < gap/2.
    half = 360 / slices / 2
    local = (ang + half) % (360 / slices) - half  # angle relative to slice center
    edge = np.radians(half - np.abs(local))
    in_slice = (r >= r_in) & (r <= r_out) & (r * np.sin(edge) > gap / 2)
    selected = in_slice & (np.abs(((ang + 180) % 360) - 180) < half)

    t = np.clip((r - r_in) / (r_out - r_in), 0, 1)[..., None]
    rgb[in_slice] = (DARK_IN + (DARK_OUT - DARK_IN) * t)[in_slice]
    rgb[selected] = (ACCENT2 * (1 - t) + ACCENT * t)[selected]
    a[in_slice] = 1

    # Faint light outer edge (like RingWindow's EdgeBrush) so the ring reads on a dark taskbar.
    edge_w = 0.035 if not small else 0.08
    outer = in_slice & ~selected & (r > r_out - edge_w)
    rgb[outer] = rgb[outer] + (255 - rgb[outer]) * 0.35

    # Selection arc just outside the selected slice.
    if not small:
        arc = (r > 0.935) & (r < 0.99) & (np.abs(((ang + 180) % 360) - 180) < half - 4)
        light = ACCENT2 + (255 - ACCENT2) * 0.55
        rgb[arc] = light
        a[arc] = 1

    # Center disc with an accent rim.
    disc = r <= r_ctr
    v = np.clip((dy + r_ctr) / (2 * r_ctr), 0, 1)[..., None]
    k = np.clip((dx + dy + 2 * r_ctr) / (4 * r_ctr), 0, 1)[..., None]
    rgb[disc] = ((ACCENT + (255 - ACCENT) * 0.15) * (1 - k) + ACCENT2 * k)[disc]
    # Soft top highlight so the filled disc does not read as a flat dot.
    rgb[disc] = (rgb + (255 - rgb) * 0.12 * (1 - v))[disc]
    a[disc] = 1

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
