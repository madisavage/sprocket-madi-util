using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using MadiLib;
using Sprocket.DamageModelling;

namespace Subcaliber
{
    /// <summary>
    /// Long-rod penetrators (at least <see cref="MinCalibers"/> long) penetrate
    /// by the Lanz-Odermatt perforation equation instead of the game's de Marre,
    /// and spall more.
    /// </summary>
    /// <remarks>
    /// The game's penetration is de Marre, <c>100 (√m v / ((d/100)^0.75 K))^1.43</c>
    /// mm, per plate with the penetrator's speed at that plate. On impact, the
    /// penetrator constant <c>K</c> is replaced by the one that makes de Marre
    /// give Lanz-Odermatt's penetration at the impact speed; everything after
    /// (obliquity, plates, spall) is still the game's.
    /// </remarks>
    internal static class LongRod
    {
        /// <summary>Shortest penetrator, in calibers, the equation holds for.</summary>
        internal const float MinCalibers = 4f;

        internal static ConfigEntry<float> TargetHardness = null!;
        internal static ConfigEntry<float> SpallMultiplier = null!;

        /// <summary>Lanz-Odermatt coefficients per penetrator material, by material name.</summary>
        private static Dictionary<string, Coefficients> materials = new();

        /// <summary>
        /// Each fired penetrator by its flight mass and diameter, which is all
        /// the game's penetration sees of it.
        /// </summary>
        internal static readonly Dictionary<(float Mass, float Diameter), (float Calibers, string Material)> Rods = new();

        private static ManualLogSource log = null!;

        internal static void Load(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            TargetHardness = config.Bind("Long rod", "Target hardness", 260f, new ConfigDescription(
                "Brinell hardness of the armour long rods are rated against. Rolled homogeneous armour is about 260; the equation holds from 150 to 500.",
                new AcceptableValueRange<float>(150f, 500f), new Step(10f)));
            SpallMultiplier = config.Bind("Long rod", "Spall multiplier", 1.25f, new ConfigDescription(
                "Scales each spall burst a long rod makes: 2 is two of the game's bursts, 1.25 is one and a quarter-mass one. 1 is the game's, 0 none.",
                new AcceptableValueRange<float>(0f, 3f), new Step(0.05f)));
            SpallMultiplier.SettingChanged += (_, _) => ArmourSimulationPatch.Resimulate();

            string path = Path.Combine(Paths.ConfigPath, "madi.subcaliber.longrod.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, Coefficients>
                {
                    ["Steel"] = Coefficients.Steel,
                    ["Tungsten carbide"] = Coefficients.Tungsten,
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

            try
            {
                SpallDetour.Apply();
            }
            catch (Exception e)
            {
                log.LogError($"Couldn't hook spall, long rods spall as the game's: {e}");
            }
        }

        /// <summary>
        /// Lanz-Odermatt penetration (mm of armour at <see cref="TargetHardness"/>,
        /// at normal) of a penetrator <paramref name="calibers"/> long and
        /// <paramref name="diameter"/> mm wide, of <paramref name="material"/>,
        /// at <paramref name="speed"/> m/s; null outside the equation's range.
        /// </summary>
        /// <remarks>
        /// <c>P/L = a / tanh(b0 + b1 L/D) √(ρp/ρt) e^(−s²/v²)</c>, ρ in kg/m³, v
        /// in km/s. Obliquity (<c>cos^m θ</c>) is left to the game.
        /// </remarks>
        internal static float? Penetration(float calibers, float diameter, string material, float speed)
        {
            if (calibers < MinCalibers)
                return null;
            Coefficients c = materials.TryGetValue(material, out Coefficients? found) ? found : Coefficients.Tungsten;
            float density = Plugin.Materials.TryGetValue(material, out float d) ? d : Plugin.SteelDensity;
            float bhn = TargetHardness.Value;
            float s2 = (c.K != 0f
                ? c.C0 * MathF.Pow(bhn, c.K) * MathF.Pow(c.Hardness, c.N)
                : (c.C0 + c.C1 * bhn) * bhn) / density;
            float v = speed / 1000f;
            if (v * v < s2 / 1.5f)
                return null;
            return calibers * diameter * c.A / MathF.Tanh(0.283f + 0.0656f * calibers)
                * MathF.Sqrt(density / Plugin.SteelDensity) * MathF.Exp(-s2 / (v * v));
        }

        /// <summary>
        /// On a combat impact, gives a long rod the penetrator constant that
        /// makes the game's de Marre equal Lanz-Odermatt at the impact speed,
        /// and marks the hit for <see cref="SpallDetour"/>. Leaves anything
        /// else alone, and is idempotent.
        /// </summary>
        internal static void Hit(ref PenetratorInfo penetrator)
        {
            PenetratorInfo p = penetrator;
            if (!Rods.TryGetValue((p.Mass, p.Diameter), out var rod))
                return;
            float diameter = p.Diameter * 1000f;
            float speed = p.Velocity.magnitude;
            if (Penetration(rod.Calibers, diameter, rod.Material, speed) is not { } penetration)
                return;
            // de Marre solved for K: P = 100 (√m v / ((d/100)^0.75 K))^1.43
            float k = MathF.Sqrt(p.Mass) * speed / (MathF.Pow(diameter * 0.01f, 0.75f) * MathF.Pow(penetration / 100f, 1f / 1.43f));
            ushort constant = (ushort)Math.Clamp(MathF.Round(k), 1f, ushort.MaxValue);
            penetrator = new PenetratorInfo(p.Mass, p.Diameter, p.Density, p.MaxNormalizationAngle, constant, p.Position, p.Velocity);
            SpallDetour.LongRod = true;
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

    /// <summary>
    /// Repeats each spall burst during a long-rod hit, by a native detour on
    /// <c>CompoundStructure.CreateSpallBurst(PenetrationSimulation, ref SpallSpawnBurst)</c>.
    /// </summary>
    /// <remarks>
    /// Harmony can't patch it: Il2CppInterop's trampoline breaks on a byref
    /// struct it wraps as a class, which <c>SpallSpawnBurst</c> is. The detour
    /// takes the raw pointers instead and writes the field at its native offset.
    /// </remarks>
    internal static class SpallDetour
    {
        /// <summary>Set while a long rod's hit simulates.</summary>
        internal static bool LongRod;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CreateSpallBurst(IntPtr simulation, IntPtr burst, IntPtr method);

        // Kept alive for as long as the detour points at it
        private static CreateSpallBurst? hook, original;
        private static INativeDetour? detour;
        private static int volume;

        internal static unsafe void Apply()
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(AccessTools.Method(typeof(CompoundStructure), nameof(CompoundStructure.CreateSpallBurst)));
            IntPtr method = (IntPtr)field!.GetValue(null)!;
            // Field offsets count the object header, which a struct behind a pointer doesn't have
            volume = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(Il2CppClassPointerStore<CompoundStructure.SpallSpawnBurst>.NativeClassPtr, "volume")) - 2 * IntPtr.Size;
            hook = Hook;
            // MethodInfo starts with its native code pointer
            detour = INativeDetour.CreateAndApply(*(IntPtr*)method, hook, out original);
        }

        private static unsafe void Hook(IntPtr simulation, IntPtr burst, IntPtr method)
        {
            if (!LongRod || burst == IntPtr.Zero)
            {
                original!(simulation, burst, method);
                return;
            }
            // A burst spawns a fixed number of fragments, its volume their mass:
            // whole bursts for the whole multiplier, then one for the rest
            float multiplier = Subcaliber.LongRod.SpallMultiplier.Value;
            float* v = (float*)((byte*)burst + volume);
            float game = *v;
            for (int i = 1; i <= multiplier; i++)
                original!(simulation, burst, method);
            float rest = multiplier - MathF.Floor(multiplier);
            if (rest > 0.001f)
            {
                *v = game * rest;
                original!(simulation, burst, method);
                *v = game;
            }
        }
    }
}
