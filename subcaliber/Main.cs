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
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.VehicleDesigner.ArmourEditing;
using Sprocket.VehicleDesigner.UI;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Weapons.Cannons;
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
    /// <see cref="Penetrator.Launch"/>. Combat penetration reads the
    /// definition's mass and diameter, so in flight the definition carries the
    /// penetrator's own.
    /// </remarks>
    [BepInPlugin("madi.subcaliber", "subcaliber", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("sprocket.dynamicsight", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BasePlugin
    {
        internal const float SteelDensity = 7850f;

        internal static ConfigEntry<float> MaxVelocity = null!;
        internal static ConfigEntry<float> PropellantDensity = null!;

        // Per shell type, keyed by projectile GUID
        internal static KeyedConfig<float> Diameters = null!;
        internal static KeyedConfig<float> Lengths = null!;
        internal static KeyedConfig<string> MaterialNames = null!;
        internal static KeyedConfig<float> SabotCoverages = null!;
        internal static KeyedConfig<float> SabotDensities = null!;

        /// <summary>Penetrator materials and their densities (kg/m³), in file order.</summary>
        internal static Dictionary<string, float> Materials = new();

        public override void Load()
        {
            MaxVelocity = Config.Bind("Muzzle velocity", "Max muzzle velocity", 2200f, new ConfigDescription(
                "Muzzle velocity in m/s that rounds approach but never pass, as powder gas can only push so fast. Rounds well below it are barely changed.",
                new AcceptableValueRange<float>(1000f, 4000f), new Step(50f)));
            PropellantDensity = Config.Bind("Muzzle velocity", "Propellant density", 800f, new ConfigDescription(
                "Charge mass per volume of case, in kg/m³. Pushing its own gas slows light projectiles: full-bore rounds are unchanged, a light sabot round loses a lot. 0 is the game's, no charge mass.",
                new AcceptableValueRange<float>(0f, 2000f), new Step(50f)));
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

            Diameters = new KeyedConfig<float>(Config, "Projectile diameter", 0f, "Projectile diameter in mm; 0 fires full-bore.");
            Lengths = new KeyedConfig<float>(Config, "Penetrator length", 3f, "Penetrator length in calibers of its own diameter.");
            MaterialNames = new KeyedConfig<string>(Config, "Penetrator material", "Steel", "Penetrator material, from madi.subcaliber.materials.json.");
            SabotDensities = new KeyedConfig<float>(Config, "Sabot density", 0.35f, "Sabot density relative to steel (aluminium is about 0.35).");
            SabotCoverages = new KeyedConfig<float>(Config, "Sabot coverage", 1f, "Fraction of the penetrator's length the sabot covers; 1 is all of it.");
            new Harmony("madi.subcaliber").PatchAll();
        }

        /// <summary>The penetrator shells of type <paramref name="guid"/> fire from a gun of <paramref name="caliber"/> mm.</summary>
        internal static Penetrator Penetrator(string guid, float caliber)
        {
            float diameter = Diameters[guid];
            return new Penetrator(
                diameter > 0f ? Math.Min(diameter / caliber, 1f) : 1f,
                Lengths[guid],
                Materials.TryGetValue(MaterialNames[guid], out float density) ? density / SteelDensity : 1f,
                SabotDensities[guid],
                SabotCoverages[guid]);
        }
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
    /// Sizes the projectile definition for each shot: launch values while the
    /// gun fires, penetrator values once it has.
    /// </summary>
    [HarmonyPatch(typeof(CannonBehaviour), nameof(CannonBehaviour.FireInternal))]
    internal static class CannonFirePatch
    {
        // The game's values for each projectile definition, before any scaling.
        private static readonly Dictionary<IntPtr, (float Mass, float Diameter, float Length, float Drag)> originals = new();

        /// <summary>
        /// Before the shot: full-bore diameter so the charge fills the gun, and a
        /// length that gives the projectile the mass of penetrator and sabot.
        /// </summary>
        /// <param name="__instance">The gun firing.</param>
        /// <param name="__0">The type of projectile it fires.</param>
        /// <param name="__state">The definition, for the postfix.</param>
        private static void Prefix(CannonBehaviour __instance, ProjectileTypeID __0, out ProjectileDefinition? __state)
        {
            __state = __instance.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource?.GetProjectileDefinition(__0);
            if (__state != null)
                Size(__state, launch: true);
        }

        /// <summary>After the shot: the penetrator's own diameter and length, for flight and penetration.</summary>
        private static void Postfix(ProjectileDefinition? __state)
        {
            if (__state != null)
                Size(__state, launch: false);
        }

        /// <summary>
        /// Sizes <paramref name="definition"/> for its penetrator: in launch
        /// state (full bore, penetrator and sabot mass as length) or flight
        /// state (the penetrator's own diameter and length).
        /// </summary>
        internal static void Size(ProjectileDefinition definition, bool launch)
        {
            if (!originals.TryGetValue(definition.Pointer, out var game))
                originals[definition.Pointer] = game = (definition.Mass, definition.Diameter, definition.Length, definition.dragConstant);
            Penetrator p = Plugin.Penetrator(definition.Guid.ToString(), game.Diameter * 1000f);
            var dragCurve = ProjectileDefinition.ShellDragCoefficientCurve;

            definition.Mass = game.Mass * p.Mass;
            definition.dragConstant = game.Drag * p.Ratio * p.Ratio * dragCurve.Evaluate(game.Diameter * p.Ratio * 1000f) / dragCurve.Evaluate(game.Diameter * 1000f);
            definition.Diameter = game.Diameter * (launch ? 1f : p.Ratio);
            definition.Length = game.Length * (launch ? p.Launch : p.Length);
            if (!launch)
                LongRod.Rods[(definition.Mass, definition.Diameter)] = (p.Calibers, Plugin.MaterialNames[definition.Guid.ToString()]);
        }
    }

    /// <summary>
    /// Compatibility with Dynamic Gun Sight (sprocket.dynamicsight), whose
    /// <c>AmmoCatalog</c> reads a round's mass and drag from its projectile
    /// definition once, and predicts muzzle velocity by running the
    /// definition's propellant functions outside a shot. Both see whichever
    /// state the definition was last left in, so each is given the right one.
    /// </summary>
    [HarmonyPatch]
    internal static class DynamicSightPatch
    {
        private static readonly Type? catalog = Type.GetType("SprocketDynamicSight.Game.AmmoCatalog, SprocketDynamicSight");

        private static bool Prepare() => catalog != null;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(catalog, "Load"),
            AccessTools.Method(catalog, "TryPredictVelocity"),
        };

        /// <summary>
        /// <c>Load(int typeId)</c> caches mass and drag, so it reads flight
        /// state; <c>TryPredictVelocity(RoundInfo, CannonBehaviour)</c>
        /// simulates the charge, so it reads launch state.
        /// </summary>
        private static void Prefix(System.Reflection.MethodBase __originalMethod, object __0, out ProjectileDefinition? __state)
        {
            bool launch = __originalMethod.Name == "TryPredictVelocity";
            int id = launch ? Traverse.Create(__0).Field<int>("TypeId").Value : (int)__0;
            __state = IProjectileTypeRegister.Instance is { } register && register.TryGet(new ProjectileTypeID(id), out IProjectileType type)
                ? type?.TryCast<ProjectileDefinition>()
                : null;
            if (__state != null)
                CannonFirePatch.Size(__state, launch);
        }

        /// <summary>Leaves the definition in flight state, as after a shot.</summary>
        private static void Postfix(ProjectileDefinition? __state)
        {
            if (__state != null)
                CannonFirePatch.Size(__state, launch: false);
        }
    }

    /// <summary>
    /// Gives the cannon the sabot round's muzzle velocity, which the gunner
    /// AI leads and drops its shots by. The game caches the full-bore
    /// shell's on the cannon when it is built.
    /// </summary>
    [HarmonyPatch(typeof(Cannon), nameof(Cannon.Build))]
    internal static class CannonBuildPatch
    {
        private static void Postfix(Cannon __instance)
        {
            if (__instance.Blueprint is not { } gun || __instance.ShellBlueprint is not { } shell)
                return;
            Penetrator p = Plugin.Penetrator(CannonEditorOnGuiPatch.Guid(shell), gun.Caliber);
            __instance.muzzleVelocity = gun.MuzzleVelocity * CannonEditorOnGuiPatch.VelocityRatio(gun, p.Launch);
        }
    }

    /// <summary>
    /// Adds penetrator diameter, length and material, and the resulting round's
    /// performance, to the cannon editor panel.
    /// </summary>
    [HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    internal static class CannonEditorOnGuiPatch
    {
        // Shell slot, (Diameter, Length, SabotCoverage, SabotDensity, Readouts); one entry for each shell slot
        private static readonly ObjectCache<(FloatProperty Diameter, FloatProperty Length, FloatProperty SabotCoverage, FloatProperty SabotDensity, StringProperty[] Readouts)> properties = new();

        /// <summary>
        /// Runs after the game draws the cannon panel and appends the controls
        /// and readouts, reusing one set of properties per shell slot.
        /// </summary>
        /// <remarks>
        /// The game builds the panel once per selection, so the readouts are
        /// bound properties each control refreshes, not baked text.
        /// </remarks>
        /// <param name="__instance">The cannon editor being drawn.</param>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        private static void Postfix(CannonEditor __instance, IGUILayout __0)
        {
            CannonBlueprint? gun = __instance.Component?.Blueprint;
            ShellSlotBlueprint? shell = __instance.Component?.ShellBlueprint;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (gun == null || shell?.generatedProjectileBlueprint == null || shell.generatedProjectileBlueprint.Length == 0 || drawer == null)
                return;
            var set = properties.Get(shell, () =>
            {
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Sabot muzzle velocity", () => $"{Round(gun, shell).Velocity:0} m/s", ReadoutDescription),
                    EditorGui.Readout("Sabot penetration", () => $"{Round(gun, shell).Penetration:0} mm", ReadoutDescription),
                    EditorGui.Readout("Penetrator / sabot mass", () => $"{Round(gun, shell).Penetrator:0.00} / {Round(gun, shell).Sabot:0.00} kg", ReadoutDescription),
                };
                return (
                    new FloatProperty(
                        "Projectile diameter",
                        (Func<float>)(() => gun.Caliber * Penetrator(gun, shell).Ratio),
                        (Action<float>)(value => Set(shell, Plugin.Diameters, value, readouts)),
                        "Diameter of the penetrator the shell fires, in mm. Below the caliber it is a discarding sabot round.")
                    {
                        Min = 1f,
                        Step = 1f
                    },
                    new FloatProperty(
                        "Penetrator length",
                        (Func<float>)(() => Penetrator(gun, shell).Calibers),
                        (Action<float>)(value => Set(shell, Plugin.Lengths, value, readouts)),
                        "Penetrator length in calibers of its own diameter. The game's shells are 3.")
                    {
                        Min = 1f,
                        Max = 30f,
                        Step = 0.5f
                    },
                    new FloatProperty(
                        "Sabot coverage",
                        (Func<float>)(() => Penetrator(gun, shell).SabotCoverage),
                        (Action<float>)(value => Set(shell, Plugin.SabotCoverages, value, readouts)),
                        "Fraction of the penetrator's length the sabot covers. 1 is all of it, as an APDS pot sabot; a long rod's saddle sabot grips about a quarter to a third.")
                    {
                        Min = 0.05f,
                        Max = 1f,
                        Step = 0.05f
                    },
                    new FloatProperty(
                        "Sabot density",
                        (Func<float>)(() => Penetrator(gun, shell).SabotDensity),
                        (Action<float>)(value => Set(shell, Plugin.SabotDensities, value, readouts)),
                        "Sabot density relative to steel (aluminium is about 0.35). The sabot is discarded at the muzzle.")
                    {
                        Min = 0f,
                        Max = 1f,
                        Step = 0.05f
                    },
                    readouts);
            });
            if (!EditorGui.Section(drawer, "subcaliber.cannon", "Penetrator", __instance.RequestRedraw))
                return;
            set.Diameter.Max = gun.Caliber;
            drawer.Float(set.Diameter);
            drawer.Float(set.Length);
            drawer.Float(set.SabotCoverage);
            drawer.Float(set.SabotDensity);

            MaterialDropdown(drawer, Plugin.MaterialNames[Guid(shell)], name => Set(shell, Plugin.MaterialNames, name, set.Readouts));

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

        /// <summary>Saves a setting for every projectile type of the shell slot and refreshes the readouts.</summary>
        private static void Set<T>(ShellSlotBlueprint shell, KeyedConfig<T> setting, T value, StringProperty[] readouts)
        {
            foreach (ShellBlueprint projectile in shell.generatedProjectileBlueprint)
                setting[projectile.ProjectileTypeGuid.ToString()] = value;
            EditorGui.Refresh(readouts);
        }

        private const string ReadoutDescription = "Penetration is at the muzzle. Mass is per round; the sabot is discarded at the muzzle.";

        private static Penetrator Penetrator(CannonBlueprint gun, ShellSlotBlueprint shell) => Plugin.Penetrator(Guid(shell), gun.Caliber);

        /// <summary>
        /// Muzzle velocity, penetration at the muzzle, and penetrator and sabot
        /// mass (kg) of the shell's round, from the game's own formulas.
        /// </summary>
        private static (float Velocity, float Penetration, float Penetrator, float Sabot) Round(CannonBlueprint gun, ShellSlotBlueprint shell)
        {
            Penetrator p = Penetrator(gun, shell);
            float fullBore = ShellProperties.GetProjectileMass(gun.Caliber);
            float velocity = gun.MuzzleVelocity * VelocityRatio(gun, p.Launch);
            float penetration = LongRod.Penetration(p.Calibers, gun.Caliber * p.Ratio, fullBore * p.Mass, Plugin.MaterialNames[Guid(shell)], velocity, gun.PenetratorConstant);
            return (velocity, penetration, fullBore * p.Mass, fullBore * p.Sabot);
        }

        internal static string Guid(ShellSlotBlueprint shell) => shell.generatedProjectileBlueprint[0].ProjectileTypeGuid.ToString();

        /// <summary>
        /// Muzzle velocity of a projectile <paramref name="length"/> times the
        /// full-bore length over a full-bore projectile's, from the game's
        /// interior ballistics.
        /// </summary>
        internal static float VelocityRatio(CannonBlueprint gun, float length)
        {
            float caliber = gun.Caliber;
            float Velocity(float l) => ShellProperties.CalculateFireInfo(gun.BoreLength, caliber, 3f * caliber * l, gun.PropellantLength, gun.OperatingPressure).MuzzleVelocity;
            return Velocity(length) / Velocity(1f);
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
    [HarmonyPatch(typeof(ArmourOverlayConfig), nameof(ArmourOverlayConfig.Draw))]
    internal static class ArmourOverlayConfigDrawPatch
    {
        internal static float Calibers = 3f;
        internal static string Material = "Steel";

        private static FloatProperty? length;

        /// <summary>Mass of the simulated penetrator over the game's steel, 3-caliber one.</summary>
        internal static float MassScale =>
            Calibers / 3f * (Plugin.Materials.TryGetValue(Material, out float density) ? density / Plugin.SteelDensity : 1f);

        /// <summary>Runs after the game draws the armour view panel and appends the controls.</summary>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        /// <param name="__instance">The armour view panel being drawn.</param>
        private static void Postfix(ArmourOverlayConfig __instance, IGUILayout __0)
        {
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null)
                return;
            if (!EditorGui.Section(drawer, "subcaliber.armour", "Penetrator", () => __instance.GUIRedrawRequired = true))
                return;
            length ??= new FloatProperty(
                "Penetrator length",
                (Func<float>)(() => Calibers),
                (Action<float>)(value =>
                {
                    Calibers = value;
                    ArmourSimulationPatch.Resimulate();
                }),
                "Simulated penetrator length in calibers. The game's shells are 3.")
            {
                Min = 1f,
                Max = 30f,
                Step = 0.5f
            };
            drawer.Float(length);
            CannonEditorOnGuiPatch.MaterialDropdown(drawer, Material, name =>
            {
                Material = name;
                ArmourSimulationPatch.Resimulate();
            });
        }
    }

    /// <summary>
    /// Marks the armour view's own simulations, so <see cref="PenetratePatch"/>
    /// only changes those and not combat.
    /// </summary>
    [HarmonyPatch(typeof(ArmourOverlayPointerOperator), nameof(ArmourOverlayPointerOperator.Update))]
    internal static class ArmourSimulationPatch
    {
        internal static bool Simulating;
        private static ArmourOverlayPointerOperator? last;

        /// <summary>Forgets the last simulated shot so the armour view runs it again with new settings.</summary>
        internal static void Resimulate()
        {
            if (last == null)
                return;
            last.lastSampleSettings = default;
            last.visualizedDamageModelHash = 0;
        }

        private static void Prefix(ArmourOverlayPointerOperator __instance)
        {
            last = __instance;
            Simulating = true;
        }

        private static void Postfix() => Simulating = false;
    }

    /// <summary>
    /// Scales the armour view's simulated penetrator; see
    /// <see cref="ArmourOverlayConfigDrawPatch"/>. In combat, hands each hit to
    /// <see cref="LongRod.Hit"/>. The structure is patched on every entry point
    /// the simulator may call; the first one to run consumes the flag, so the
    /// shot is scaled once.
    /// </summary>
    [HarmonyPatch]
    internal static class PenetratePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            nameof(CompoundStructure.Penetrate),
            nameof(CompoundStructure.Sprocket_DamageModelling_IReadOnlyCompoundStructure_Penetrate),
            nameof(CompoundStructure.Sprocket_DamageModelling_IDamageModelStructure_Penetrate),
        }.Select(name => (System.Reflection.MethodBase)AccessTools.Method(typeof(CompoundStructure), name));

        private static void Prefix(ref PenetratorInfo __0)
        {
            if (!ArmourSimulationPatch.Simulating)
            {
                LongRod.Hit(ref __0);
                return;
            }
            ArmourSimulationPatch.Simulating = false;
            // The armour view's penetration is whatever the panel asks for; a long rod only spalls more
            SpallDetour.LongRod = ArmourOverlayConfigDrawPatch.Calibers >= LongRod.MinCalibers;
            float k = ArmourOverlayConfigDrawPatch.MassScale;
            if (k == 1f)
                return;
            PenetratorInfo p = __0;
            __0 = new PenetratorInfo(p.Mass * k, p.Diameter, p.Density, p.MaxNormalizationAngle, p.PenetratorConstant, p.Position, p.Velocity / MathF.Sqrt(k));
        }

        /// <summary>Ends a long rod's hit; the innermost entry point runs the simulation and ends first.</summary>
        private static void Postfix() => SpallDetour.LongRod = LongRod.Hitting = false;
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
