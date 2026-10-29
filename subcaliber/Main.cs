extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.VehicleDesigner.ArmourEditing;
using Sprocket.VehicleDesigner.UI;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace Subcaliber
{
    /// <summary>
    /// BepInEx entry point. Shells can fire a penetrator of their own diameter,
    /// length and material. Narrower than the gun, it is a discarding sabot
    /// round: the full-bore charge pushes penetrator and sabot, and the
    /// penetrator then flies and hits at its own diameter and mass.
    /// </summary>
    /// <remarks>
    /// The game sizes a projectile from one diameter <c>D</c>: steel, <c>3D</c>
    /// long, in a <c>D</c>-wide case. Muzzle velocity comes from
    /// <c>ShellProperties.CalculateFireInfo(bore, D, length, case)</c>, where
    /// projectile mass is <c>D² × length</c>, so a <c>D</c>-wide steel projectile
    /// of the same mass stands in for any launch mass: see
    /// <see cref="Penetrator.Launch"/>. Each sabot round fires its own
    /// projectile type through <see cref="Loadout"/>, which flies and hits
    /// with the penetrator's own mass and diameter.
    /// </remarks>
    [BepInPlugin("madi.subcaliber", "subcaliber", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", ">=" + ModInfo.Version)]
    [BepInDependency("sprocket.dynamicsight", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BasePlugin
    {
        internal const float SteelDensity = 7850f;

        /// <summary>Lightest sabot, relative to steel: below it a sabot would be free weight.</summary>
        internal const float MinSabotDensity = 0.3f;

        internal static ConfigEntry<float> MaxVelocity = null!;
        internal static ConfigEntry<float> PropellantDensity = null!;

        /// <summary>Penetrator materials and their densities (kg/m³), in file order.</summary>
        internal static Dictionary<string, float> Materials = new();

        public override void Load()
        {
            MaxVelocity = Config.Bind("Muzzle velocity", "Max muzzle velocity", 2200f, new ConfigDescription(
                "Muzzle velocity in m/s that rounds approach but never pass, as powder gas can only push so fast. Rounds well below it are barely changed.",
                new AcceptableValueRange<float>(1000f, 40000f), new SoftMax(4000f), new Step(50f)));
            PropellantDensity = Config.Bind("Muzzle velocity", "Propellant density", 800f, new ConfigDescription(
                "Charge mass per volume of case, in kg/m³. Pushing its own gas slows light projectiles: full-bore rounds are unchanged, a light sabot round loses a lot. 0 is the game's, no charge mass.",
                new AcceptableValueRange<float>(0f, 20000f), new SoftMax(2000f), new Step(50f)));
            LongRod.Load(Config, Log);
            SettingsTab.AddSection("Subcaliber", MaxVelocity, PropellantDensity, LongRod.TargetHardness, LongRod.SpallMultiplier);

            string path = Path.Combine(Paths.ConfigPath, "madi.subcaliber.materials.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, float>
                {
                    ["Steel"] = SteelDensity,
                    ["Tungsten carbide"] = 15600f,
                    ["Tungsten heavy alloy"] = 17600f,
                    ["Depleted uranium"] = 19100f,
                }, new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                Materials = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Log.LogError($"Couldn't read {path}, penetrators are steel: {e.Message}");
            }

            using (var apds = typeof(Plugin).Assembly.GetManifestResourceStream("apds.png"))
                Loadout.AddIcon("sabot.apds", apds!);
            // The type's own icon is a long rod's; shorter penetrators get APDS's
            using (var icon = typeof(Plugin).Assembly.GetManifestResourceStream("sabot.png"))
                Loadout.Register(SabotRound.Type, "Sabot", SabotEditor.Draw, Shape, LongRod.Hit, icon,
                    (owner, id) => Round(owner, id).Length < LongRod.MinCalibers ? "sabot.apds" : null);
            Impacts.Simulated += ArmourView.Simulate;
            Impacts.ArmourPanel += ArmourView.Draw;
            new Harmony("madi.subcaliber").PatchAll();
        }

        /// <summary>
        /// A sabot round's projectile: the penetrator's own mass, diameter and
        /// length in flight, and at launch the mass of penetrator and sabot as
        /// the length of a full-bore steel projectile that heavy.
        /// </summary>
        private static RoundShape? Shape(ProjectileDefinition game, Cannon cannon, string id, List<ProjectileFunctionDefinition> functions)
        {
            if (cannon.Blueprint is not { } owner)
                return null;
            Penetrator p = Penetrator(Round(owner, id), game.Diameter * 1000f);
            var dragCurve = ProjectileDefinition.ShellDragCoefficientCurve;
            return new RoundShape
            {
                Mass = game.Mass * p.Mass,
                Diameter = game.Diameter * p.Ratio,
                Length = game.Length * p.Length,
                LaunchLength = game.Length * p.Launch,
                Drag = game.dragConstant * p.Ratio * p.Ratio * dragCurve.Evaluate(game.Diameter * p.Ratio * 1000f) / dragCurve.Evaluate(game.Diameter * 1000f),
            };
        }

        /// <summary>The sabot round <paramref name="id"/> in <paramref name="owner"/>; a full-bore steel one if it isn't one.</summary>
        internal static SabotRound Round(CannonBlueprint owner, string id) =>
            Loadout.Get<SabotRound>(owner, id, SabotRound.Type) ?? new SabotRound();

        /// <summary>The penetrator <paramref name="round"/> fires from a gun of <paramref name="caliber"/> mm.</summary>
        internal static Penetrator Penetrator(SabotRound round, float caliber) => new(
            round.Diameter > 0f ? Math.Min(round.Diameter / caliber, 1f) : 1f,
            round.Length,
            Materials.TryGetValue(round.Material ?? "", out float density) ? density / SteelDensity : 1f,
            Math.Max(round.SabotDensity, MinSabotDensity),
            round.SabotCoverage);
    }

    /// <summary>
    /// A penetrator: <see cref="Ratio"/> of the caliber wide, <see cref="Calibers"/>
    /// of its own diameter long, <see cref="Density"/> times steel's density, in
    /// a sabot of <see cref="SabotDensity"/> times steel's covering
    /// <see cref="SabotCoverage"/> of its length. Masses are relative to the
    /// game's full-bore projectile (steel, 3 calibers).
    /// </summary>
    internal readonly record struct Penetrator(float Ratio, float Calibers, float Density, float SabotDensity, float SabotCoverage)
    {
        /// <summary>Penetrator mass.</summary>
        public float Mass => Ratio * Ratio * Ratio * Calibers / 3f * Density;

        /// <summary>Sabot mass: the bore around the penetrator, along <see cref="SabotCoverage"/> of its length.</summary>
        public float Sabot => SabotDensity * Ratio * (1f - Ratio * Ratio) * Calibers * SabotCoverage / 3f;

        /// <summary>Everything the charge pushes, as the length of a full-bore steel projectile that heavy.</summary>
        public float Launch => Mass + Sabot;

        /// <summary>Penetrator length.</summary>
        public float Length => Ratio * Calibers / 3f;
    }

    /// <summary>
    /// Draws a sabot round's penetrator diameter, length and material, and its
    /// performance, under the round in the cannon panel's <b>Rounds</b> section.
    /// </summary>
    internal static class SabotEditor
    {
        // Cannon blueprint and round id, (Diameter, Length, SabotCoverage, SabotDensity, Readouts); one entry for each sabot round
        private static readonly Dictionary<(IntPtr, string), (FloatProperty Diameter, FloatProperty Length, FloatProperty SabotCoverage, FloatProperty SabotDensity, StringProperty[] Readouts)> properties = new();

        /// <summary>
        /// Draws the round's controls and readouts, reusing one set of
        /// properties per round.
        /// </summary>
        /// <remarks>
        /// The game builds the panel once per selection, so the readouts are
        /// bound properties each control refreshes, not baked text.
        /// </remarks>
        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            if (!properties.TryGetValue((gun.Pointer, id), out var set))
            {
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Muzzle velocity", () => $"{Performance(gun, id).Velocity:0} m/s", ReadoutDescription),
                    EditorGui.Readout("Penetration", () => $"{Performance(gun, id).Penetration:0} mm ({Regime(gun, id)})", ReadoutDescription),
                    EditorGui.Readout("Penetrator / sabot mass", () => $"{Performance(gun, id).Penetrator:0.00} / {Performance(gun, id).Sabot:0.00} kg", ReadoutDescription),
                };
                void Set(Action<SabotRound> change)
                {
                    Loadout.Edit(gun, id, change);
                    changed();
                    EditorGui.Refresh(readouts);
                }
                properties[(gun.Pointer, id)] = set = (
                    new FloatProperty(
                        "Projectile diameter",
                        (Func<float>)(() => gun.Caliber * Penetrator(gun, id).Ratio),
                        (Action<float>)(value => Set(r => r.Diameter = value)),
                        "Diameter of the penetrator the round fires, in mm. Below the caliber it is a discarding sabot round.")
                    {
                        Min = 1f,
                        Step = 1f
                    },
                    new FloatProperty(
                        "Penetrator length",
                        (Func<float>)(() => Penetrator(gun, id).Calibers),
                        (Action<float>)(value => Set(r => r.Length = value)),
                        "Penetrator length in calibers of its own diameter. The game's shells are 3.")
                    {
                        Min = 1f,
                        Max = 300f,
                        SoftMin = 1f,
                        SoftMax = 30f,
                        UseSoftLimits = true,
                        Step = 0.5f
                    },
                    new FloatProperty(
                        "Sabot coverage",
                        (Func<float>)(() => Penetrator(gun, id).SabotCoverage),
                        (Action<float>)(value => Set(r => r.SabotCoverage = value)),
                        "Fraction of the penetrator's length the sabot covers. 1 is all of it, as an APDS pot sabot; a long rod's saddle sabot grips about a quarter to a third.")
                    {
                        Min = 0.05f,
                        Max = 1f,
                        Step = 0.05f
                    },
                    new FloatProperty(
                        "Sabot density",
                        (Func<float>)(() => Penetrator(gun, id).SabotDensity),
                        (Action<float>)(value => Set(r => r.SabotDensity = value)),
                        "Sabot density relative to steel (aluminium is about 0.35). The sabot is discarded at the muzzle.")
                    {
                        Min = Plugin.MinSabotDensity,
                        Max = 10f,
                        SoftMin = Plugin.MinSabotDensity,
                        SoftMax = 1f,
                        UseSoftLimits = true,
                        Step = 0.05f
                    },
                    readouts);
            }
            set.Diameter.Max = gun.Caliber;
            drawer.Float(set.Diameter);
            drawer.Float(set.Length);
            drawer.Float(set.SabotCoverage);
            drawer.Float(set.SabotDensity);

            MaterialDropdown(drawer, Plugin.Round(gun, id).Material, name =>
            {
                Loadout.Edit<SabotRound>(gun, id, r => r.Material = name);
                changed();
                EditorGui.Refresh(set.Readouts);
            });

            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }

        /// <summary>Draws a dropdown of <see cref="Plugin.Materials"/> with <paramref name="selected"/> chosen.</summary>
        internal static void MaterialDropdown(IGUIElementDrawer drawer, string selected, Action<string> onSelect)
        {
            List<string> names = Plugin.Materials.Keys.ToList();
            EditorGui.Dropdown(
                drawer,
                "Material",
                names,
                names.IndexOf(selected),
                i => onSelect(names[i]),
                $"Densities are in {Path.Combine(Paths.ConfigPath, "madi.subcaliber.materials.json")}; edit it and restart to add materials.");
        }

        private const string ReadoutDescription = "Penetration is at the muzzle: the game's for a full-bore round, else the Lanz-Odermatt long-rod equation, raised for stubby (APDS-like) penetrators. Mass is per round; the sabot is discarded at the muzzle.";

        /// <summary>Which penetration model the round's <b>Penetration</b> readout comes from.</summary>
        private static string Regime(CannonBlueprint gun, string id)
        {
            Penetrator p = Penetrator(gun, id);
            return p.Ratio >= 1f ? "full-bore" : p.Calibers < LongRod.MinCalibers ? "APDS" : "long rod";
        }

        private static Penetrator Penetrator(CannonBlueprint gun, string id) => Plugin.Penetrator(Plugin.Round(gun, id), gun.Caliber);

        /// <summary>
        /// Muzzle velocity, penetration at the muzzle, and penetrator and sabot
        /// mass (kg) of the round, from the game's own formulas.
        /// </summary>
        private static (float Velocity, float Penetration, float Penetrator, float Sabot) Performance(CannonBlueprint gun, string id)
        {
            Penetrator p = Penetrator(gun, id);
            float fullBore = ShellProperties.GetProjectileMass(gun.Caliber);
            float velocity = gun.MuzzleVelocity * Loadout.VelocityRatio(gun, p.Launch);
            float penetration = LongRod.Penetration(p.Ratio, p.Calibers, gun.Caliber * p.Ratio, fullBore * p.Mass, Plugin.Round(gun, id).Material, velocity, gun.PenetratorConstant);
            return (velocity, penetration, fullBore * p.Mass, fullBore * p.Sabot);
        }
    }

    /// <summary>
    /// Adds penetrator length and material to the armour view's penetration
    /// simulator, so its shot carries the same mass a penetrator of that length
    /// and material would.
    /// </summary>
    /// <remarks>
    /// The simulator fires a steel, 3-caliber projectile of the panel's caliber,
    /// at the speed that gives the panel's penetration. With these set, its
    /// mass is scaled by length and density and its speed lowered to keep the
    /// same penetration (de Marre: <c>√m v</c>), so only spall and other
    /// mass/speed effects change. The game's penetrator density field is left
    /// alone, as in combat.
    /// </remarks>
    internal static class ArmourView
    {
        internal static float Calibers = 3f;
        internal static string Material = "Steel";

        private static FloatProperty? length;

        /// <summary>Mass of the simulated penetrator over the game's steel, 3-caliber one.</summary>
        internal static float MassScale =>
            Calibers / 3f * (Plugin.Materials.TryGetValue(Material, out float density) ? density / Plugin.SteelDensity : 1f);

        /// <summary>
        /// The armour view's shot: its penetration is whatever the panel asks
        /// for, so the penetrator's mass is scaled and its speed lowered to
        /// keep it, and a long rod spalls more.
        /// </summary>
        internal static void Simulate(ref PenetratorInfo penetrator)
        {
            if (Calibers >= LongRod.MinCalibers)
                Impacts.SpallBursts = LongRod.SpallMultiplier.Value;
            float k = MassScale;
            if (k == 1f)
                return;
            PenetratorInfo p = penetrator;
            penetrator = new PenetratorInfo(p.Mass * k, p.Diameter, p.Density, p.MaxNormalizationAngle, p.PenetratorConstant, p.Position, p.Velocity / MathF.Sqrt(k));
        }

        /// <summary>Draws the section, from <see cref="Impacts.ArmourPanel"/>.</summary>
        internal static void Draw(IGUIElementDrawer drawer, Action redraw)
        {
            if (!EditorGui.Section(drawer, "subcaliber.armour", "Penetrator", redraw))
                return;
            length ??= new FloatProperty(
                "Penetrator length",
                (Func<float>)(() => Calibers),
                (Action<float>)(value =>
                {
                    Calibers = value;
                    Impacts.Resimulate();
                }),
                "Simulated penetrator length in calibers. The game's shells are 3.")
            {
                Min = 1f,
                Max = 300f,
                SoftMin = 1f,
                SoftMax = 30f,
                UseSoftLimits = true,
                Step = 0.5f
            };
            drawer.Float(length);
            SabotEditor.MaterialDropdown(drawer, Material, name =>
            {
                Material = name;
                Impacts.Resimulate();
            });
        }
    }

    /// <summary>
    /// Slows light projectiles by the charge's own mass, then caps muzzle
    /// velocity softly at <see cref="Plugin.MaxVelocity"/>. The game's
    /// velocity goes as <c>1/√mass</c>, as if the charge's energy all went
    /// into the projectile, so a light sabot round would leave far faster
    /// than powder gas can push it.
    /// </summary>
    /// <remarks>
    /// Charge mass, by the Lagrange approximation: a third of the charge
    /// moves at projectile speed, so <c>v ∝ 1/√(M + C/3)</c>. Scaled so the
    /// game's full-bore projectile (3 calibers long) is unchanged:
    /// <c>v √((1 + C/3M₃) / (1 + C/3M))</c>, with the charge taken as the
    /// case's length at bore width, at <see cref="Plugin.PropellantDensity"/>.
    /// Cap: <c>v / (1 + (v/vmax)⁸)^⅛</c>; under about 0.75 vmax a round loses
    /// well under 1%, and no round passes vmax. Every muzzle velocity the
    /// game works out, in combat, the designer and Dynamic Gun Sight, comes
    /// from here.
    /// </remarks>
    [HarmonyPatch(typeof(ShellProperties), nameof(ShellProperties.CalculateFireInfo), typeof(float), typeof(float), typeof(float), typeof(float), typeof(uint))]
    internal static class VelocityCapPatch
    {
        /// <param name="__1">Caliber, mm.</param>
        /// <param name="__2">Projectile length, mm, as a full-bore steel projectile of its mass.</param>
        /// <param name="__3">Case length, mm.</param>
        private static void Postfix(float __1, float __2, float __3, ref FireInfo __result)
        {
            // Charge mass over a third, in mm of full-bore steel projectile
            float charge = __3 * Plugin.PropellantDensity.Value / Plugin.SteelDensity / 3f;
            float v = __result.MuzzleVelocity * MathF.Sqrt((1f + charge / (3f * __1)) / (1f + charge / __2));
            __result.MuzzleVelocity = v / MathF.Pow(1f + MathF.Pow(v / Plugin.MaxVelocity.Value, 8f), 0.125f);
        }
    }
}
