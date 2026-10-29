using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MadiLib;
using Rounds;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;

namespace Subcaliber
{
    /// <summary>
    /// Subcaliber penetrators penetrate by the Lanz-Odermatt long-rod
    /// equation, times <see cref="LengthFactor"/> by length. Full-bore rounds
    /// keep the game's de Marre.
    /// Long rods (at least <see cref="MinCalibers"/>) also spall more.
    /// </summary>
    /// <remarks>
    /// The game's penetration is de Marre, <c>100 (√m v / ((d/100)^0.75 K))^1.43</c>
    /// mm, per plate with the penetrator's speed at that plate. On impact, the
    /// penetrator constant <c>K</c> is replaced by the one that makes de Marre
    /// give <see cref="Penetration"/> at the impact speed; everything after
    /// (obliquity, plates, spall) is still the game's. De Marre overrates
    /// small fast penetrators (about 1.5x for real APDS, 2-3x for APFSDS), so
    /// it isn't used for them. Against real rounds Lanz-Odermatt underrates
    /// APDS (3-4 calibers, about 1.35x) and overrates early rods (8-10
    /// calibers, about 0.85x), hence <see cref="LengthFactor"/>. Fitting both
    /// costs a rod up to about 6% as it lengthens from 4 to 8 calibers; a
    /// linear blend of de Marre into Lanz-Odermatt cost 14%. See
    /// tools/penetration.py.
    /// </remarks>
    internal static class LongRod
    {
        /// <summary>
        /// Real penetration over Lanz-Odermatt's, by length in calibers:
        /// linear between points, flat beyond the ends. Fitted to
        /// tools/penetration.py's rounds.
        /// </summary>
        internal static readonly (float Calibers, float Factor)[] LengthFactor = { (3.5f, 1.35f), (8.5f, 0.85f), (20f, 0.95f) };

        /// <summary>Shortest penetrator, in calibers, that spalls as a long rod.</summary>
        internal const float MinCalibers = 8f;

        internal static ConfigEntry<float> TargetHardness = null!;
        internal static ConfigEntry<float> SpallMultiplier = null!;

        /// <summary>Lanz-Odermatt coefficients per penetrator material, by material name.</summary>
        private static Dictionary<string, Coefficients> materials = new();

        private static ManualLogSource log = null!;

        internal static void Load(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            TargetHardness = config.Bind("Long rod", "Target hardness", 260f, new ConfigDescription(
                "Brinell hardness of the armour long rods are rated against. Rolled homogeneous armour is about 260; the equation holds from 150 to 500.",
                new AcceptableValueRange<float>(150f, 500f), new Step(10f)));
            SpallMultiplier = config.Bind("Long rod", "Spall multiplier", 1.25f, new ConfigDescription(
                "Scales each spall burst a long rod makes: 2 is two of the game's bursts, 1.25 is one and a quarter-mass one. 1 is the game's, 0 none.",
                new AcceptableValueRange<float>(0f, 30f), new SoftMax(3f), new Step(0.05f)));
            SpallMultiplier.SettingChanged += (_, _) => Impacts.Resimulate();

            string path = Path.Combine(Paths.ConfigPath, "madi.subcaliber.longrod.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, Coefficients>
                {
                    ["Steel"] = Coefficients.Steel,
                    // Fitted to the 17pdr's APDS and 3BM3 in tools/penetration.py: brittle (low A), but hard, so less slowed by speed (low C0)
                    ["Tungsten carbide"] = new() { A = 0.70f, C0 = 90.2f, C1 = -0.148f },
                    ["Tungsten heavy alloy"] = Coefficients.Tungsten,
                    ["Depleted uranium"] = new() { A = 0.825f, C0 = 90.0f, C1 = -0.0849f },
                }, new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                materials = JsonSerializer.Deserialize<Dictionary<string, Coefficients>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                log.LogError($"Couldn't read {path}, long rods use tungsten coefficients: {e.Message}");
            }
        }

        /// <summary>
        /// Penetration (mm, at normal) of a penetrator <paramref name="ratio"/>
        /// of the caliber wide, <paramref name="calibers"/> long,
        /// <paramref name="diameter"/> mm wide and <paramref name="mass"/> kg, of
        /// <paramref name="material"/>, at <paramref name="speed"/> m/s from a
        /// gun with <paramref name="penetratorConstant"/>: the game's de Marre
        /// if full-bore, else Lanz-Odermatt times <see cref="Factor"/>.
        /// </summary>
        internal static float Penetration(float ratio, float calibers, float diameter, float mass, string material, float speed, float penetratorConstant) =>
            ratio >= 1f
                ? Impacts.DeMarre(diameter, mass, speed, penetratorConstant)
                : LanzOdermatt(calibers, diameter, material, speed) * Factor(calibers);

        /// <summary><see cref="LengthFactor"/> at <paramref name="calibers"/>.</summary>
        internal static float Factor(float calibers)
        {
            var points = LengthFactor;
            if (calibers <= points[0].Calibers)
                return points[0].Factor;
            for (int i = 1; i < points.Length; i++)
                if (calibers <= points[i].Calibers)
                    return points[i - 1].Factor + (points[i].Factor - points[i - 1].Factor) * (calibers - points[i - 1].Calibers) / (points[i].Calibers - points[i - 1].Calibers);
            return points[^1].Factor;
        }

        /// <summary>
        /// Lanz-Odermatt penetration (mm of armour at <see cref="TargetHardness"/>,
        /// at normal) of a penetrator <paramref name="calibers"/> long and
        /// <paramref name="diameter"/> mm wide, of <paramref name="material"/>,
        /// at <paramref name="speed"/> m/s.
        /// </summary>
        /// <remarks>
        /// <c>P/L = a / tanh(b0 + b1 L/D) √(ρp/ρt) e^(−s²/v²)</c>, ρ in kg/m³, v
        /// in km/s. Obliquity (<c>cos^m θ</c>) is left to the game. Below the
        /// equation's speed range (<c>v² &lt; s²/1.5</c>) it's still used, as it
        /// falls smoothly there; falling back to de Marre made a rod
        /// penetrate more as it slowed.
        /// </remarks>
        private static float LanzOdermatt(float calibers, float diameter, string material, float speed)
        {
            Coefficients c = materials.TryGetValue(material ?? "", out Coefficients? found) ? found : Coefficients.Tungsten;
            float density = Plugin.Materials.TryGetValue(material ?? "", out float d) ? d : Plugin.SteelDensity;
            float bhn = TargetHardness.Value;
            float s2 = (c.K != 0f
                ? c.C0 * MathF.Pow(bhn, c.K) * MathF.Pow(c.Hardness, c.N)
                : (c.C0 + c.C1 * bhn) * bhn) / density;
            float v = speed / 1000f;
            return calibers * diameter * c.A / MathF.Tanh(0.283f + 0.0656f * calibers)
                * MathF.Sqrt(density / Plugin.SteelDensity) * MathF.Exp(-s2 / (v * v));
        }

        /// <summary>
        /// A sabot round's hit: the penetrator constant that makes the game's
        /// de Marre give <see cref="Penetration"/> at the impact speed, and a
        /// long rod's extra spall.
        /// </summary>
        internal static void Hit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id)
        {
            SabotRound round = Plugin.Round(owner, id);
            PenetratorInfo p = penetrator;
            Impacts.SetPenetration(ref penetrator, Penetration(Plugin.Penetrator(round, owner.Caliber).Ratio, round.Length, p.Diameter * 1000f, p.Mass, round.Material, p.Velocity.magnitude, p.PenetratorConstant));
            if (round.Length >= MinCalibers)
                Impacts.SpallBursts = SpallMultiplier.Value;
        }
    }

    /// <summary>
    /// Lanz-Odermatt coefficients for a penetrator material. With <see cref="K"/>
    /// zero, the tungsten/DU form <c>s² = (c0 + c1 BHNt) BHNt / ρp</c>; otherwise
    /// the steel form <c>s² = c0 BHNt^k BHNp^n / ρp</c>, with the penetrator's
    /// own <see cref="Hardness"/>.
    /// </summary>
    internal sealed class Coefficients
    {
        public float A { get; set; }
        public float C0 { get; set; }
        public float C1 { get; set; }
        public float K { get; set; }
        public float N { get; set; }
        public float Hardness { get; set; }

        internal static readonly Coefficients Tungsten = new() { A = 0.994f, C0 = 134.5f, C1 = -0.148f };
        internal static readonly Coefficients Steel = new() { A = 1.104f, C0 = 9874f, K = 0.3598f, N = -0.2342f, Hardness = 500f };
    }
}
