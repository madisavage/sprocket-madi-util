# /// script
# requires-python = ">=3.10"
# dependencies = ["Pillow"]
# ///
"""Draws the HUD status icons for stagedAmmo, stabilizer and hydropneumatic, in shellicons.py's style.
They're light grey with a dark outline, so the HUD tints them by state (GUI.color multiplies).

Run: uv run tools/hudicons.py            (writes each plugin's icons/<name>.png)
     uv run tools/hudicons.py sheet.png  (also writes every icon side by side, to look over)
"""
import math
import sys

from PIL import Image

from shellicons import OUTLINE, ROOT, SIZE, Shell, px

LIGHT = (236, 236, 236, 255)
MID = (170, 170, 170, 255)


class Icon(Shell):
    """A Shell canvas with circles, lines and arrows; x across from the centre, y down from the top."""

    def circle(self, x, y, r, fill, width=3):
        cx, cy = SIZE / 2 + x, y
        self.draw.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=fill, outline=OUTLINE, width=px(width))

    def line(self, points, width):
        """A thick light line with a dark outline, through points."""
        pts = [(px(SIZE / 2 + x), px(y)) for x, y in points]
        self.draw.line(pts, fill=OUTLINE, width=px(width + 6), joint="curve")
        self.draw.line(pts, fill=LIGHT, width=px(width), joint="curve")

    def arrowhead(self, x, y, angle, size=26):
        """A filled arrowhead whose base covers the line end at (x, y), pointing along angle (radians, 0 = right, y down)."""
        x, y = x + size * 0.8 * math.cos(angle), y + size * 0.8 * math.sin(angle)
        back = [(x - size * math.cos(angle) + s * size * 0.7 * math.sin(angle), y - size * math.sin(angle) - s * size * 0.7 * math.cos(angle)) for s in (1, -1)]
        self.poly([(x, y)] + back, LIGHT)

    def arc(self, cx, cy, r, start, end, width=12, head_start=False, head_end=True):
        """An arc arrow round (cx, cy) from start to end degrees (0 = right, clockwise as y is down)."""
        steps = max(8, int(abs(end - start) / 4))
        angles = [math.radians(start + (end - start) * i / steps) for i in range(steps + 1)]
        self.line([(cx + r * math.cos(a), cy + r * math.sin(a)) for a in angles], width)
        sign = 1 if end > start else -1
        if head_end:
            a = angles[-1]
            self.arrowhead(cx + r * math.cos(a), cy + r * math.sin(a), a + sign * math.pi / 2)
        if head_start:
            a = angles[0]
            self.arrowhead(cx + r * math.cos(a), cy + r * math.sin(a), a - sign * math.pi / 2)

    def straight(self, x0, y0, x1, y1, width=12):
        """A straight arrow from (x0, y0) to (x1, y1)."""
        self.line([(x0, y0), (x1, y1)], width)
        self.arrowhead(x1, y1, math.atan2(y1 - y0, x1 - x0))

    def hull(self, cx, cy, tilt=0.0, scale=1.0):
        """A tank's hull and turret seen from the side, centred at (cx, cy), tilted by tilt degrees (nose up positive)."""
        t = math.radians(-tilt)

        def at(points):
            return [(cx + scale * (x * math.cos(t) - y * math.sin(t)), cy + scale * (x * math.sin(t) + y * math.cos(t))) for x, y in points]

        self.poly(at([(30, -42), (110, -38), (110, -30), (30, -30)]), LIGHT)  # gun
        self.poly(at([(-40, -48), (34, -48), (44, -24), (-50, -24)]), LIGHT)  # turret
        self.poly(at([(-96, -24), (96, -24), (110, 0), (86, 26), (-86, 26), (-104, 0)]), MID)  # hull


def traverse():
    s = Icon()
    s.arc(0, 140, 96, 200, 340, head_start=True)
    s.rect(9, 30, 128, LIGHT)  # gun
    s.circle(0, 140, 54, LIGHT)  # turret ring, from above
    s.circle(0, 140, 16, MID)
    return s


def elevation():
    s = Icon()
    s.rect(110, 196, 226, MID)  # trunnion base
    s.poly([(-40, 176), (-14, 140), (96, 60), (106, 74), (-4, 158), (-20, 196)], LIGHT)  # raised gun
    s.circle(-30, 182, 26, MID)  # trunnion
    s.arc(-30, 182, 130, -78, -20, head_start=True)
    return s


def azimuth():
    s = Icon()
    s.arc(0, 196, 120, 230, 310, head_start=True)
    s.rect(10, 50, 176, LIGHT)  # gun, from above
    s.rect(44, 160, 220, MID)  # mantlet
    return s


def autoloader():
    s = Icon()
    s.circle(0, 128, 100, MID)
    for i in range(6):
        a = math.radians(90 + 60 * i)
        s.circle(64 * math.cos(a), 128 + 64 * math.sin(a), 26, LIGHT)
    s.circle(0, 128, 18, LIGHT)
    return s


def loader():
    s = Icon()
    s.poly([(-92, 236), (-84, 176), (-48, 148), (48, 148), (84, 176), (92, 236)], MID)  # shoulders
    s.circle(0, 88, 52, LIGHT)  # head
    s.rect(70, 50, 72, MID)  # helmet brim
    return s


def stowage():
    s = Icon()
    s.rect(110, 120, 236, MID)  # rack
    for x in (-66, 0, 66):  # three shells standing in it
        s.poly([(x - 24, 112), (x - 24, 220), (x + 24, 220), (x + 24, 112)], LIGHT)
        s.poly([(x - 24, 114), (x - 18, 70), (x, 40), (x + 18, 70), (x + 24, 114)], LIGHT)
    return s


def height():
    s = Icon()
    s.hull(-24, 160, scale=0.9)
    s.straight(96, 140, 96, 36)
    s.straight(96, 166, 96, 218)
    return s


def pitch():
    s = Icon()
    s.hull(0, 160, tilt=14, scale=0.95)
    s.arc(0, 160, 112, 300, 350, head_start=True)
    return s


def roll():
    s = Icon()
    a = math.radians(-12)

    def at(points):
        return [(x * math.cos(a) - (y - 150) * math.sin(a), 150 + x * math.sin(a) + (y - 150) * math.cos(a)) for x, y in points]

    s.poly(at([(-50, 76), (50, 76), (62, 118), (-62, 118)]), LIGHT)  # turret, from the front
    s.circle(*at([(0, 98)])[0], 12, MID)  # gun muzzle
    s.poly(at([(-104, 118), (104, 118), (112, 170), (-112, 170)]), MID)  # hull
    s.poly(at([(-112, 170), (-60, 170), (-60, 206), (-112, 206)]), LIGHT)  # tracks
    s.poly(at([(60, 170), (112, 170), (112, 206), (60, 206)]), LIGHT)
    s.arc(0, 150, 116, 200, 250, head_start=True)
    return s


def hullaim():
    s = Icon()
    s.draw.ellipse([px(48), px(48), px(208), px(208)], outline=OUTLINE, width=px(24))
    s.draw.ellipse([px(54), px(54), px(202), px(202)], outline=LIGHT, width=px(12))
    for x0, y0, x1, y1 in [(0, 12, 0, 84), (0, 172, 0, 244), (-116, 128, -44, 128), (44, 128, 116, 128)]:
        s.line([(x0, y0), (x1, y1)], 12)
    s.circle(0, 128, 12, LIGHT)
    return s


ICONS = {
    "stabilizer/icons/traverse.png": traverse,
    "stabilizer/icons/elevation.png": elevation,
    "stabilizer/icons/azimuth.png": azimuth,
    "stagedAmmo/icons/autoloader.png": autoloader,
    "stagedAmmo/icons/loader.png": loader,
    "stagedAmmo/icons/stowage.png": stowage,
    "hydropneumatic/icons/height.png": height,
    "hydropneumatic/icons/pitch.png": pitch,
    "hydropneumatic/icons/roll.png": roll,
    "hydropneumatic/icons/hullaim.png": hullaim,
}


def main():
    paths = [make().save(ROOT / name) for name, make in ICONS.items()]
    for path in paths:
        print(path.relative_to(ROOT))
    if len(sys.argv) > 1:
        sheet = Image.new("RGBA", (SIZE * len(paths), SIZE * 2), (64, 64, 64, 255))
        tints = [(255, 255, 255), (77, 230, 89), (255, 209, 51), (242, 64, 51), (140, 140, 140)]
        for i, path in enumerate(paths):
            icon = Image.open(path)
            sheet.alpha_composite(icon, (SIZE * i, 0))
            # second row: each tint the HUD uses, small, to check they read
            for j, tint in enumerate(tints):
                r, g, b, a = icon.split()
                tinted = Image.merge("RGBA", [c.point(lambda v, k=k: v * k // 255) for c, k in zip((r, g, b), tint)] + [a])
                small = tinted.resize((SIZE // 5, SIZE // 5), Image.LANCZOS)
                sheet.alpha_composite(small, (SIZE * i + (SIZE // 5) * j, SIZE + SIZE // 3))
        sheet.save(sys.argv[1])


if __name__ == "__main__":
    main()
