using System;
using System.Collections.Generic;
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
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;

namespace HighExplosive
{
    /// <summary>
    /// BepInEx entry point. Adds the HE round type: a full-bore shell of any
    /// mass that detonates on contact with the game's own explosive, and whose
    /// hits penetrate by mass alone and spall catastrophically; HESH, the
    /// same with its own normalization and penetration; the HEAT
    /// round type (see <see cref="Heat"/>); and the APHE round type (see
    /// <see cref="Aphe"/>).
    /// </summary>
    [BepInPlugin("madi.highexplosive", "highExplosive", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ConfigEntry<float> ExplosivePower = null!;
        internal static ConfigEntry<float> Penetration = null!;
        internal static ConfigEntry<float> Spall = null!;
        internal static ConfigEntry<float> MaxBursts = null!;
        internal static ConfigEntry<float> Normalization = null!;
        internal static ConfigEntry<float> HeshPenetration = null!;
        internal static ConfigEntry<float> HeshNormalization = null!;

        /// <summary>Registers the HE round type and its settings.</summary>
        public override void Load()
        {
            ExplosivePower = Config.Bind("High explosive", "Explosive power", 0f, new ConfigDescription(
                "Explosive power of a full-bore HE shell, times its shell mass. At 0 the shell still explodes on impact but the blast does no damage. The game's APHE shells are 1000. The game's blast acts as if the shell were inside, so it goes through armour the shell doesn't penetrate.",
                new AcceptableValueRange<float>(0f, 20000f), new Step(100f)));
            Penetration = Config.Bind("High explosive", "Penetration", 20f, new ConfigDescription(
                "Thickest plate in mm a 1 kg HE shell blows through, spalling the inside; the shell itself goes no further. It goes as the cube root of shell mass, whatever the speed: 8 kg is twice 1 kg.",
                new AcceptableValueRange<float>(0f, 200f), new Step(1f)));
            Spall = Config.Bind("High explosive", "Spall multiplier", 16f, new ConfigDescription(
                "Spall from an HE hit, times its shell mass: as heavier fragments, and more bursts up to Max spall bursts. 1 is the game's.",
                new AcceptableValueRange<float>(0f, 100f), new Step(1f)));
            MaxBursts = Config.Bind("High explosive", "Max spall bursts", 1f, new ConfigDescription(
                "Most spall bursts per one the game makes; the rest of Spall multiplier goes into heavier fragments. The explosion already makes a few hundred bursts per hit, so each extra one costs frame rate. 1 adds none.",
                new AcceptableValueRange<float>(1f, 8f), new Step(0.25f)));
            ExplosivePower.SettingChanged += (_, _) => Loadout.Invalidate();
            Normalization = Config.Bind("High explosive", "Normalization", 30f, new ConfigDescription(
                "Degrees an HE hit turns toward the plate's normal on impact, as its blast spreads over the face: sloped plates and roofs count for less. 90 meets every plate square on; never less than the game's own.",
                new AcceptableValueRange<float>(0f, 90f), new Step(5f)));
            SettingsTab.AddSection("High explosive", ExplosivePower, Penetration, Spall, MaxBursts, Normalization);
            HeshPenetration = Config.Bind("HESH", "Penetration", 25f, new ConfigDescription(
                "Thickest plate in mm a 1 kg HESH shell blows through, as High explosive's Penetration; its squash head gets a little more from the plate.",
                new AcceptableValueRange<float>(0f, 200f), new Step(1f)));
            HeshNormalization = Config.Bind("HESH", "Normalization", 90f, new ConfigDescription(
                "Degrees a HESH hit turns toward the plate's normal, as its head squashes onto the face. 90 meets every plate square on, so slope doesn't help.",
                new AcceptableValueRange<float>(0f, 90f), new Step(5f)));
            SettingsTab.AddSection("HESH", HeshPenetration, HeshNormalization);
            foreach (HeKind kind in new[] { HeKind.He, HeKind.Hesh })
                using (var icon = Icon(kind.Type))
                    Loadout.Register(kind.Type, kind.Label, (drawer, cannon, id, changed) => Editor.Draw(drawer, cannon, id, changed, kind), (game, cannon, id, functions) =>
                        cannon.Blueprint is { } owner ? Shell(game, functions, Round(owner, id, kind).Mass) : null,
                        (ref PenetratorInfo penetrator, CannonBlueprint owner, string id) => Hit(ref penetrator, owner, id, kind), icon);
            Heat.Load(Config, Log);
            Aphe.Load(Config, Log);
            Impacts.Simulated += ArmourView.Simulate;
            Impacts.ArmourPanel += ArmourView.Draw;
            new Harmony("madi.highexplosive").PatchAll();
        }

        internal static HeRound Round(CannonBlueprint owner, string id, HeKind kind) => Loadout.Get<HeRound>(owner, id, kind.Type) ?? new HeRound();

        /// <summary>This plugin's icon for round type <paramref name="type"/>, embedded from <c>icons/&lt;type&gt;.png</c>.</summary>
        internal static System.IO.Stream? Icon(string type) => typeof(Plugin).Assembly.GetManifestResourceStream(type + ".png");

        /// <summary>
        /// An HE, HEAT or APHE round's projectile: the game's shape at <paramref name="m"/>
        /// times its mass (longer, as the game sizes mass by length), without
        /// any explosive it had; unless <paramref name="explode"/> is false,
        /// with the game's, detonating on contact, in its place. At
        /// <see cref="ExplosivePower"/> 0 it does no blast damage but still
        /// explodes on impact, ending the shell's flight there.
        /// </summary>
        internal static RoundShape Shell(ProjectileDefinition game, List<ProjectileFunctionDefinition> functions, float m, bool explode = true)
        {
            functions.RemoveAll(f => f.Identifier == "HE");
            // Always carried, at 0 too: it's also the shell's explosion effect
            if (explode)
                functions.Insert(functions.FindIndex(f => f.Identifier == "AP") + 1, new HighExplosiveProjectileFunctionDefinition(0f, ExplosivePower.Value * m));
            return new RoundShape
            {
                Mass = game.Mass * m,
                Diameter = game.Diameter,
                Length = game.Length * m,
                LaunchLength = game.Length * m,
                Drag = game.dragConstant,
            };
        }

        /// <summary>
        /// An HE or HESH hit: it blows through plates up to its penetration,
        /// from mass alone, but goes no further than the first one's far face;
        /// then <see cref="Blast"/>.
        /// </summary>
        private static void Hit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id, HeKind kind)
        {
            Impacts.SetPenetration(ref penetrator, kind.Penetration * MathF.Cbrt(penetrator.Mass));
            Blast(ref penetrator, Round(owner, id, kind).Mass, kind.Normalization);
            Fuze.StartAtExit();
        }

        /// <summary>
        /// An HE hit's blast for a shell <paramref name="m"/> times full-bore:
        /// turned toward the plate by <paramref name="normalization"/> degrees, and spall
        /// split evenly between more bursts and heavier fragments, but bursts
        /// no more than <see cref="MaxBursts"/>, the rest in heavier fragments.
        /// </summary>
        internal static void Blast(ref PenetratorInfo penetrator, float m, float normalization)
        {
            Impacts.Normalize(ref penetrator, normalization);
            (Impacts.SpallBursts, Impacts.FragmentMass) = Split(Spall.Value * m);
        }

        /// <summary>
        /// <paramref name="spall"/> times the game's spall as bursts and
        /// fragment mass: evenly between the two, but bursts no more than
        /// <see cref="MaxBursts"/>, the rest in heavier fragments.
        /// </summary>
        internal static (float Bursts, float FragmentMass) Split(float spall)
        {
            float bursts = Math.Clamp(MathF.Sqrt(spall), Math.Min(1f, spall), MaxBursts.Value);
            return (bursts, bursts > 0f ? spall / bursts : 0f);
        }
    }

    /// <summary>HE or HESH: the round type, and its penetration and normalization settings.</summary>
    internal sealed record HeKind(string Type, string Label, Func<float> PenetrationOf, Func<float> NormalizationOf)
    {
        internal static readonly HeKind He = new(HeRound.Type, "HE", () => Plugin.Penetration.Value, () => Plugin.Normalization.Value);
        internal static readonly HeKind Hesh = new("hesh", "HESH", () => Plugin.HeshPenetration.Value, () => Plugin.HeshNormalization.Value);

        /// <summary>Penetration in mm of a 1 kg shell.</summary>
        internal float Penetration => PenetrationOf();

        internal float Normalization => NormalizationOf();
    }

    /// <summary>An HE or HESH round, as stored in the vehicle file through <see cref="Loadout"/>.</summary>
    internal sealed class HeRound
    {
        /// <summary>HE's round type in <see cref="Loadout"/>; HESH's is <see cref="HeKind.Hesh"/>'s.</summary>
        internal const string Type = "he";

        /// <summary>Shell mass over the game's full-bore shell for the gun.</summary>
        public float Mass { get; set; } = 1f;
    }

    /// <summary>Draws an HE or HESH round's shell mass and its performance in the cannon panel's <b>Rounds</b> section.</summary>
    internal static class Editor
    {
        // Cannon blueprint, round id and kind, (Mass, Readouts); one entry for each HE or HESH round
        private static readonly Dictionary<(IntPtr, string, HeKind), (FloatProperty Mass, StringProperty[] Readouts)> properties = new();

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed, HeKind kind)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            if (!properties.TryGetValue((gun.Pointer, id, kind), out var set))
            {
                HeRound R() => Plugin.Round(gun, id, kind);
                float Kg() => ShellProperties.GetProjectileMass(gun.Caliber) * R().Mass;
                const string description = "Penetration is the same at any range. Spall and explosive scale with shell mass.";
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Muzzle velocity", () => $"{Velocity(gun, R().Mass):0} m/s", description),
                    EditorGui.Readout("Penetration", () => $"{kind.Penetration * MathF.Cbrt(Kg()):0} mm", description),
                    EditorGui.Readout("Shell weight", () => $"{Kg():0.00} kg", description),
                };
                properties[(gun.Pointer, id, kind)] = set = (
                    new FloatProperty(
                        "Shell mass",
                        (Func<float>)(() => R().Mass),
                        (Action<float>)(value =>
                        {
                            Loadout.Edit<HeRound>(gun, id, r => r.Mass = value);
                            changed();
                            EditorGui.Refresh(readouts);
                        }),
                        "Shell mass over the game's full-bore shell for this gun. Heavier shells leave slower, and explode and spall harder.")
                    {
                        Min = 0.25f,
                        Max = 4f,
                        Step = 0.05f
                    },
                    readouts);
            }
            drawer.Float(set.Mass);
            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }

        /// <summary>Muzzle velocity of a shell <paramref name="mass"/> times the full-bore one's, from the game's interior ballistics.</summary>
        internal static float Velocity(CannonBlueprint gun, float mass)
        {
            float caliber = gun.Caliber;
            float At(float m) => ShellProperties.CalculateFireInfo(gun.BoreLength, caliber, 3f * caliber * m, gun.PropellantLength, gun.OperatingPressure).MuzzleVelocity;
            return gun.MuzzleVelocity * At(mass) / At(1f);
        }
    }
}
