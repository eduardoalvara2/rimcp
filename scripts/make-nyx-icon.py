"""Generate the Nyx icon: a gold crescent moon with a small four-point star and an indigo glow.

Run from the repo root: python scripts/make-nyx-icon.py
Output: RiMCP/Textures/UI/RiMCP/Nyx.png (64x64, transparent background).
Drawn at 4x and downscaled with Lanczos for smooth edges.
"""
import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 64
SCALE = 4
S = SIZE * SCALE
GOLD = (244, 206, 112, 255)
GOLD_EDGE = (255, 238, 178, 255)
INDIGO = (92, 84, 214, 255)
OUT = Path(__file__).resolve().parent.parent / "RiMCP" / "Textures" / "UI" / "RiMCP" / "Nyx.png"


def p(v):
    return v * SCALE


def circle(m, cx, cy, r, fill):
    ImageDraw.Draw(m).ellipse((p(cx - r), p(cy - r), p(cx + r), p(cy + r)), fill=fill)


def crescent_mask():
    m = Image.new("L", (S, S), 0)
    circle(m, 27, 33, 21, 255)
    circle(m, 36, 28, 18, 0)  # the bite that leaves a crescent opening to the upper right
    return m


def star_mask(cx, cy, r, waist):
    m = Image.new("L", (S, S), 0)
    pts = []
    for i in range(8):
        a = math.pi / 4 * i - math.pi / 2
        rad = r if i % 2 == 0 else waist
        pts.append((p(cx) + math.cos(a) * p(rad), p(cy) + math.sin(a) * p(rad)))
    ImageDraw.Draw(m).polygon(pts, fill=255)
    return m


def paint(img, mask, color):
    layer = Image.new("RGBA", (S, S), color)
    layer.putalpha(ImageChops.multiply(mask, Image.new("L", (S, S), color[3])))
    img.alpha_composite(layer)


def main():
    shape = ImageChops.lighter(crescent_mask(), star_mask(47, 17, 9, 2.4))
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    glow = shape.filter(ImageFilter.MaxFilter(p(2) * 2 + 1)).filter(ImageFilter.GaussianBlur(p(2.2)))
    paint(img, glow.point(lambda v: min(255, int(v * 1.4))), INDIGO)
    outline = shape.filter(ImageFilter.MaxFilter(p(1) * 2 + 1))
    paint(img, outline, (60, 54, 150, 255))
    paint(img, shape, GOLD)
    # Lighter rim on the crescent's outer edge for a little depth, no gradient.
    inner = crescent_mask().filter(ImageFilter.MinFilter(int(p(1.5)) * 2 + 1))
    paint(img, ImageChops.subtract(crescent_mask(), inner), GOLD_EDGE)
    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    img.save(OUT)
    print(OUT)


main()
