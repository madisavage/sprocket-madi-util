# /// script
# requires-python = ">=3.10"
# dependencies = ["Pillow"]
# ///
"""Draws the ammo bar's round icons: upright shells on a transparent background.
Sabot rounds get sabot.png (long rod) or apds.png (shorter penetrators).

Run: uv run tools/shellicons.py            (writes each plugin's icons/<type>.png)
     uv run tools/shellicons.py sheet.png  (also writes every icon side by side, to look over)
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 256
SCALE = 4  # drawn this much bigger, then shrunk, for smooth edges
ROOT = Path(__file__).resolve().parent.parent

OUTLINE = (24, 24, 24, 255)
STEEL = (150, 156, 162, 255)
DARK_STEEL = (88, 94, 100, 255)
OLIVE = (98, 110, 62, 255)
BLACK = (40, 40, 40, 255)
COPPER = (184, 112, 60, 255)
YELLOW = (226, 190, 48, 255)
RED = (190, 52, 40, 255)
WHITE = (230, 230, 225, 255)


def px(v):
    return v * SCALE


class Shell:
    """One icon, drawn in 256-pixel units on a canvas SCALE times bigger. x is across from the centre, y down from the top."""

    def __init__(self):
        self.image = Image.new("RGBA", (px(SIZE), px(SIZE)), (0, 0, 0, 0))
        self.draw = ImageDraw.Draw(self.image)

    def poly(self, points, fill, outline=OUTLINE, width=3):
        self.draw.polygon([(px(SIZE / 2 + x), px(y)) for x, y in points], fill=fill, outline=outline, width=px(width))

    def rect(self, half, top, bottom, fill, width=3):
        self.poly([(-half, top), (half, top), (half, bottom), (-half, bottom)], fill, width=width)

    def nose(self, half, tip, shoulder, fill, blunt=0.0, steps=24):
        """An ogive from a point (or a flat of half-width blunt) at y=tip out to half at y=shoulder."""
        side = []
        for i in range(steps + 1):
            t = i / steps
            y = tip + (shoulder - tip) * t
            w = blunt + (half - blunt) * math.sqrt(1 - (1 - t) ** 2)
            side.append((w, y))
        self.poly([(-w, y) for w, y in reversed(side)] + side, fill)

    def body(self, half, shoulder, base, fill, band=COPPER):
        """The shell's body down to its base, with its driving band near the base."""
        self.rect(half, shoulder, base, fill)
        self.rect(half + 4, base - 30, base - 18, band)

    def save(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.image.resize((SIZE, SIZE), Image.LANCZOS).save(path)
        return path


def ap():
    s = Shell()
    s.body(34, 96, 236, STEEL)
    s.nose(34, 20, 98, DARK_STEEL)
    return s


def aphe():
    s = Shell()
    s.body(34, 96, 236, STEEL)
    s.rect(34, 120, 134, RED)  # explosive filler
    s.nose(34, 20, 98, DARK_STEEL)
    s.rect(14, 236, 244, BLACK)  # base fuze
    return s


def he():
    s = Shell()
    s.body(34, 106, 236, OLIVE)
    s.rect(34, 128, 142, YELLOW)
    s.nose(34, 40, 108, OLIVE)
    s.rect(10, 26, 44, DARK_STEEL)  # nose fuze
    return s


def hesh():
    s = Shell()
    s.body(34, 112, 236, OLIVE)
    s.rect(34, 132, 146, YELLOW)
    s.rect(34, 146, 156, RED)
    s.nose(34, 58, 114, WHITE, blunt=22)  # squash head: thin, blunt
    return s


def heat():
    s = Shell()
    s.body(30, 120, 236, BLACK)
    s.rect(30, 140, 154, YELLOW)
    s.poly([(-30, 122), (30, 122), (12, 66), (-12, 66)], DARK_STEEL)  # windshield
    s.rect(5, 16, 68, STEEL)  # standoff probe
    s.rect(9, 12, 22, DARK_STEEL)  # its fuze
    return s


def apds():
    s = Shell()
    # pot sabot round the whole core, with its driving band
    s.poly([(-12, 70), (-36, 96), (-36, 220), (-12, 220)], OLIVE)
    s.poly([(12, 70), (36, 96), (36, 220), (12, 220)], OLIVE)
    s.rect(38, 196, 208, COPPER)
    # short, thick tungsten core, its nose out front; the sabot's base closes behind it
    s.rect(15, 76, 200, DARK_STEEL)
    s.nose(15, 40, 78, STEEL)
    s.rect(36, 214, 226, OLIVE)
    return s


def sabot():
    s = Shell()
    # fins
    s.poly([(-9, 196), (-30, 236), (-9, 236)], DARK_STEEL)
    s.poly([(9, 196), (30, 236), (9, 236)], DARK_STEEL)
    # dart
    s.rect(8, 40, 236, DARK_STEEL)
    s.poly([(-8, 42), (0, 10), (8, 42)], STEEL)
    # sabot petals round the dart's middle
    s.poly([(-8, 88), (-36, 116), (-36, 176), (-8, 176)], OLIVE)
    s.poly([(8, 88), (36, 116), (36, 176), (8, 176)], OLIVE)
    s.rect(38, 160, 170, COPPER)
    return s


ICONS = {
    "rounds/icons/ap.png": ap,
    "highExplosive/icons/aphe.png": aphe,
    "highExplosive/icons/he.png": he,
    "highExplosive/icons/hesh.png": hesh,
    "highExplosive/icons/heat.png": heat,
    "subcaliber/icons/sabot.png": sabot,
    "subcaliber/icons/apds.png": apds,
}


def main():
    paths = [make().save(ROOT / name) for name, make in ICONS.items()]
    for path in paths:
        print(path.relative_to(ROOT))
    if len(sys.argv) > 1:
        sheet = Image.new("RGBA", (SIZE * len(paths), SIZE), (64, 64, 64, 255))
        for i, path in enumerate(paths):
            sheet.alpha_composite(Image.open(path), (SIZE * i, 0))
        sheet.save(sys.argv[1])


if __name__ == "__main__":
    main()
