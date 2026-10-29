# /// script
# requires-python = ">=3.10"
# dependencies = ["matplotlib"]
# ///
"""Real rounds against the game's de Marre and subcaliber's Lanz-Odermatt.

Run: uv run tools/penetration.py [out.png]   (no argument opens a window)

ROUNDS figures are rough, from memory: published numbers vary with range,
angle and source, and penetrator dimensions are often estimates. Replace
them with sourced values before drawing conclusions.
"""
import math
import sys

import matplotlib.pyplot as plt

STEEL = 7850
DENSITY = {"Steel": 7850, "Tungsten carbide": 15600, "Tungsten heavy alloy": 17600, "Depleted uranium": 19100}
# Lanz-Odermatt coefficients, as subcaliber's LongRod.cs defaults
LO = {
    "Steel": dict(a=1.104, c0=9874, k=0.3598, n=-0.2342, hardness=500),
    "Tungsten carbide": dict(a=0.70, c0=90.2, c1=-0.148),
    "Tungsten heavy alloy": dict(a=0.994, c0=134.5, c1=-0.148),
    "Depleted uranium": dict(a=0.825, c0=90.0, c1=-0.0849),
}
BHN = 260
FACTOR = [(3.5, 1.35), (8.5, 0.85), (20, 0.95)]  # LongRod.LengthFactor: real over Lanz-Odermatt by L/D

# kind, name, penetrator diameter mm, length mm, material, impact m/s, real mm RHA at 0°, game K
# Full-bore rows give mass in kg as length (negative) since de Marre only needs mass; K is the era's penetratorConstant.
ROUNDS = [
    ("Full-bore", "75mm M61 APC", 75, -6.8, "Steel", 618, 100, 2000),
    ("Full-bore", "17pdr APCBC", 76.2, -7.7, "Steel", 883, 170, 2000),
    ("Full-bore", "88mm PzGr 39 (KwK 36)", 88, -10.2, "Steel", 773, 155, 2000),
    ("Full-bore", "88mm PzGr 39/43 (KwK 43)", 88, -10.2, "Steel", 1000, 235, 1900),
    ("Full-bore", "122mm BR-471 (D-25T)", 122, -25.0, "Steel", 795, 190, 1800),
    ("Full-bore", "128mm PzGr 43 (PaK 44)", 128, -28.3, "Steel", 920, 265, 1800),
    ("APDS", "17pdr APDS", 47, 140, "Tungsten carbide", 1200, 230, 2000),
    ("APDS", "105mm L52 APDS (L7)", 40, 150, "Tungsten heavy alloy", 1470, 300, 1800),
    ("APDS", "120mm L15 APDS (L11)", 50, 180, "Tungsten heavy alloy", 1370, 355, 1800),
    ("APFSDS", "115mm 3BM3 (1961)", 40, 330, "Tungsten carbide", 1600, 300, 1800),
    ("APFSDS", "105mm M735 (1978)", 38, 380, "Tungsten heavy alloy", 1450, 330, 1800),
    ("APFSDS", "105mm M833 (1983)", 27, 480, "Depleted uranium", 1430, 420, 1800),
    ("APFSDS", "125mm 3BM42 (1986)", 30, 570, "Tungsten heavy alloy", 1620, 500, 1800),
    ("APFSDS", "120mm M829A1 (1990)", 27, 684, "Depleted uranium", 1500, 570, 1800),
    ("APFSDS", "120mm DM53 (L44)", 26, 745, "Tungsten heavy alloy", 1600, 650, 1800),
]


def de_marre(diameter, mass, speed, k):
    """The game's combat penetration, mm."""
    return 100 * (math.sqrt(mass) * speed / ((diameter / 100) ** 0.75 * k)) ** 1.43


def lanz_odermatt(diameter, length, material, speed):
    """Lanz-Odermatt, as subcaliber's LongRod.LanzOdermatt, mm."""
    c, rho, v = LO[material], DENSITY[material], speed / 1000
    s2 = (c["c0"] * BHN ** c["k"] * c["hardness"] ** c["n"] if "k" in c else (c["c0"] + c["c1"] * BHN) * BHN) / rho
    ld = length / diameter
    return length * c["a"] / math.tanh(0.283 + 0.0656 * ld) * math.sqrt(rho / STEEL) * math.exp(-s2 / (v * v))


def mass(diameter, length, material):
    return math.pi / 4 * (diameter / 1000) ** 2 * (length / 1000) * DENSITY[material]


def predict(kind, d, length, material, speed, k):
    m = -length if length < 0 else mass(d, length, material)
    dm = de_marre(d, m, speed, k)
    lo = lanz_odermatt(d, length, material, speed) if length > 0 else None
    return dm, lo, dm if lo is None else lo * factor(length / d)


def factor(ld):
    """subcaliber's LongRod.Factor: FACTOR interpolated, flat beyond its ends."""
    if ld <= FACTOR[0][0]:
        return FACTOR[0][1]
    for (a, fa), (b, fb) in zip(FACTOR, FACTOR[1:]):
        if ld <= b:
            return fa + (fb - fa) * (ld - a) / (b - a)
    return FACTOR[-1][1]


def main():
    fig, (ax, sweep) = plt.subplots(1, 2, figsize=(15, 7))
    colors = {"Full-bore": "tab:gray", "APDS": "tab:orange", "APFSDS": "tab:blue"}
    print(f"{'round':28} {'L/D':>5} {'real':>5} {'deMarre':>8} {'LO':>5} {'model':>5}")
    top = 0
    for kind, name, d, length, material, speed, real, k in ROUNDS:
        dm, lo, mod = predict(kind, d, length, material, speed, k)
        ld = f"{length / d:5.1f}" if length > 0 else "    -"
        print(f"{name:28} {ld} {real:5.0f} {dm:8.0f} {lo or 0:5.0f} {mod:5.0f}")
        ax.scatter(real, dm, marker="x", color=colors[kind])
        if lo is not None:
            ax.scatter(real, lo, marker="o", facecolors="none", edgecolors=colors[kind])
            ax.scatter(real, mod, marker="*", s=80, color=colors[kind])
        ax.annotate(name, (real, mod), fontsize=7, xytext=(4, 2), textcoords="offset points")
        top = max(top, real, dm, lo or 0)
    ax.plot([0, top], [0, top], "k--", lw=0.8, label="prediction = real")
    ax.scatter([], [], marker="x", color="k", label="de Marre (game)")
    ax.scatter([], [], marker="o", facecolors="none", edgecolors="k", label="Lanz-Odermatt")
    ax.scatter([], [], marker="*", s=80, color="k", label="Lanz-Odermatt x length factor (subcaliber)")
    for kind, color in colors.items():
        ax.scatter([], [], marker="s", color=color, label=kind)
    ax.set(xlabel="Real penetration, mm RHA (rough)", ylabel="Predicted, mm", title="Real rounds vs formulas", xlim=(0, top * 1.05), ylim=(0, top * 1.05))
    ax.legend(fontsize=8)

    # One rod's length swept in a 105mm gun, against the full-bore round
    d, material, gun = 25, "Depleted uranium", 105
    lds = [x / 4 for x in range(4, 121)]
    for speed, style in ((1500, "-"), (2000, "--")):
        dms = [de_marre(d, mass(d, ld * d, material), speed, 1800) for ld in lds]
        los = [lanz_odermatt(d, ld * d, material, speed) for ld in lds]
        sweep.plot(lds, dms, style, color="tab:red", label=f"de Marre @ {speed} m/s")
        sweep.plot(lds, los, style, color="tab:blue", label=f"Lanz-Odermatt @ {speed} m/s")
        sweep.plot(lds, [b * factor(ld) for ld, b in zip(lds, los)], style, color="tab:green", lw=2.5, label=f"subcaliber @ {speed} m/s")
    full_mass = 1.59e-5 * gun ** 3  # the game's full-bore projectile: steel, 3 calibers
    for speed in (1000, 1400):
        sweep.axhline(de_marre(gun, full_mass, speed, 1800), color="tab:gray", ls=":", label=f"Full-bore {gun}mm @ {speed} m/s")
    for x, _ in FACTOR:
        sweep.axvline(x, color="k", lw=0.8)
    sweep.set(xlabel="Penetrator L/D", ylabel="Penetration, mm", title=f"{d}mm {material} rod in a {gun}mm gun", ylim=(0, 1500))
    sweep.legend(fontsize=8)

    fig.tight_layout()
    if len(sys.argv) > 1:
        fig.savefig(sys.argv[1], dpi=110)
    else:
        plt.show()


if __name__ == "__main__":
    main()
