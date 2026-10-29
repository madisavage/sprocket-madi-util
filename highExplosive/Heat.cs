using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;

namespace HighExplosive
{
    /// <summary>
    /// The HEAT round type: a full-bore shell like HE, detonating on contact,
    /// whose hit is its jet instead: <see cref="JetDiameter"/> of the caliber
    /// wide, <see cref="JetMass"/> of the shell's mass, penetrating
    /// <see cref="HeatRound.Penetration"/> calibers whatever the speed. The
    /// game works out obliquity and spall for the jet; each plate takes its
    /// line-of-sight thickness off the jet's penetration, and each gap after
    /// a plate <see cref="AirLoss"/> (see <see cref="JetBudget"/>).
    /// </summary>
    internal static class Heat
    {
        internal static ConfigEntry<float> JetDiameter = null!;
        internal static ConfigEntry<float> JetMass = null!;
        internal static ConfigEntry<float> FirstSpall = null!;
        internal static ConfigEntry<float> AirLoss = null!;
        internal static ConfigEntry<float> BreakupFragments = null!;
        internal static ConfigEntry<float> BreakupMass = null!;
        internal static ConfigEntry<float> BreakupCone = null!;
        internal static ConfigEntry<bool> LogHits = null!;
        internal static ManualLogSource Log = null!;

        internal static void Load(ConfigFile config, ManualLogSource log)
        {
            Log = log;
            // A hit that throws skips HitSimulated, which ends the jet otherwise
            Impacts.HitEnded += JetBudget.End;
            JetDiameter = config.Bind("HEAT", "Jet diameter", 0.2f, new ConfigDescription(
                "Width of a HEAT round's jet as it hits, over the caliber. Narrower jets spall in a tighter cone.",
                new AcceptableValueRange<float>(0.05f, 1f), new Step(0.05f)));
            JetMass = config.Bind("HEAT", "Jet mass", 0.2f, new ConfigDescription(
                "Mass of a HEAT round's jet as it hits, over the shell's. Spall goes with it.",
                new AcceptableValueRange<float>(0.01f, 1f), new Step(0.01f)));
            FirstSpall = config.Bind("HEAT", "First spall burst", 8f, new ConfigDescription(
                "Spall from the first plate a HEAT jet perforates, times the game's, as the jet and the plate behind it break up: heavier fragments, and more bursts up to High explosive's Max spall bursts. Plates after it spall as the game's. 1 is the game's.",
                new AcceptableValueRange<float>(0f, 50f), new Step(0.5f)));
            AirLoss = config.Bind("HEAT", "Air gap loss", 50f, new ConfigDescription(
                "Penetration a HEAT jet loses per metre it travels after perforating a plate, in mm, as it stretches and breaks up: spaced armour and the gap to the far wall count against it. 0 loses none.",
                new AcceptableValueRange<float>(0f, 500f), new Step(5f)));
            BreakupFragments = config.Bind("HEAT", "Breakup fragments", 8f, new ConfigDescription(
                "Fragments a HEAT jet breaks up into where a gap uses up its penetration, going on at its speed. 0 stops it there instead.",
                new AcceptableValueRange<float>(0f, 32f), new Step(1f)));
            BreakupMass = config.Bind("HEAT", "Breakup mass", 0.25f, new ConfigDescription(
                "Mass of a broken-up HEAT jet's fragments, all together, over the jet's as it left the last plate.",
                new AcceptableValueRange<float>(0.01f, 1f), new Step(0.01f)));
            BreakupCone = config.Bind("HEAT", "Breakup cone", 1f, new ConfigDescription(
                "Width of a broken-up HEAT jet's cone, times the cone of the plate spall it is copied from.",
                new AcceptableValueRange<float>(0.25f, 10f), new Step(0.25f)));
            LogHits = config.Bind("HEAT", "Log hits", false,
                "Logs each HEAT jet's setup and its path through the armour: each plate's residual penetration and whether the jet perforated, stopped or left the vehicle.");
            SettingsTab.AddSection("HEAT", JetDiameter, JetMass, FirstSpall, AirLoss, BreakupFragments, BreakupMass, BreakupCone, LogHits);
            Impacts.FragmentSimulating += JetBudget.Simulating;
            Impacts.FragmentSimulated += JetBudget.Simulated;
            Impacts.HitSimulated += JetLog.Simulated;
            using (var icon = Plugin.Icon(HeatRound.Type))
                Loadout.Register(HeatRound.Type, "HEAT", HeatEditor.Draw, (game, cannon, id, functions) =>
                    cannon.Blueprint is { } owner ? Plugin.Shell(game, functions, Round(owner, id).Mass) : null, Hit, icon);
        }

        internal static HeatRound Round(CannonBlueprint owner, string id) => Loadout.Get<HeatRound>(owner, id, HeatRound.Type) ?? new HeatRound();

        /// <summary>A HEAT hit: the shell's penetrator becomes its jet, with the round's penetration in calibers at any speed.</summary>
        private static void Hit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id) =>
            Jet(ref penetrator, Round(owner, id).Penetration * penetrator.Diameter * 1000f);

        /// <summary>
        /// Makes <paramref name="penetrator"/>, a HEAT shell, its jet,
        /// penetrating <paramref name="penetration"/> mm, with
        /// <see cref="FirstSpall"/> on the first plate it perforates.
        /// </summary>
        internal static void Jet(ref PenetratorInfo penetrator, float penetration)
        {
            Impacts.FirstBurst = Plugin.Split(FirstSpall.Value);
            PenetratorInfo p = penetrator;
            penetrator = new PenetratorInfo(p.Mass * JetMass.Value, p.Diameter * JetDiameter.Value, p.Density, p.MaxNormalizationAngle, p.PenetratorConstant, p.Position, p.Velocity);
            Impacts.SetPenetration(ref penetrator, penetration);
            PenetratorInfo j = penetrator;
            JetBudget.Start(j.PenetratorConstant);
            if (!LogHits.Value)
                return;
            JetLog.Pending = true;
            Log.LogInfo($"HEAT jet: shell {p.Mass:0.00} kg {p.Diameter * 1000f:0} mm at {p.Velocity.magnitude:0} m/s -> jet {j.Mass:0.000} kg {j.Diameter * 1000f:0.0} mm, "
                + $"K {p.PenetratorConstant} -> {j.PenetratorConstant}, asked {penetration:0} mm, game gives {Impacts.Penetration(j):0} mm");
        }
    }

    /// <summary>A HEAT round, as stored in the vehicle file through <see cref="Loadout"/>.</summary>
    internal sealed class HeatRound
    {
        /// <summary>Its round type in <see cref="Loadout"/>.</summary>
        internal const string Type = "heat";

        /// <summary>Shell mass over the game's full-bore shell for the gun.</summary>
        public float Mass { get; set; } = 1f;

        /// <summary>Penetration in calibers of the shell, at any speed.</summary>
        public float Penetration { get; set; } = 4.5f;
    }

    /// <summary>Draws a HEAT round's shell mass, penetration and performance in the cannon panel's <b>Rounds</b> section.</summary>
    internal static class HeatEditor
    {
        // Cannon blueprint and round id, (Mass, Penetration, Readouts); one entry for each HEAT round
        private static readonly Dictionary<(IntPtr, string), (FloatProperty Mass, FloatProperty Penetration, StringProperty[] Readouts)> properties = new();

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            if (!properties.TryGetValue((gun.Pointer, id), out var set))
            {
                const string description = "Penetration is the jet's, the same at any range.";
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Muzzle velocity", () => $"{Editor.Velocity(gun, Heat.Round(gun, id).Mass):0} m/s", description),
                    EditorGui.Readout("Penetration", () => $"{Heat.Round(gun, id).Penetration * gun.Caliber:0} mm", description),
                    EditorGui.Readout("Shell weight", () => $"{ShellProperties.GetProjectileMass(gun.Caliber) * Heat.Round(gun, id).Mass:0.00} kg", description),
                };
                void Set(Action<HeatRound> change)
                {
                    Loadout.Edit(gun, id, change);
                    changed();
                    EditorGui.Refresh(readouts);
                }
                properties[(gun.Pointer, id)] = set = (
                    new FloatProperty(
                        "Shell mass",
                        (Func<float>)(() => Heat.Round(gun, id).Mass),
                        (Action<float>)(value => Set(r => r.Mass = value)),
                        "Shell mass over the game's full-bore shell for this gun. Heavier shells leave slower; penetration doesn't change.")
                    {
                        Min = 0.25f,
                        Max = 4f,
                        Step = 0.05f
                    },
                    new FloatProperty(
                        "Penetration",
                        (Func<float>)(() => Heat.Round(gun, id).Penetration),
                        (Action<float>)(value => Set(r => r.Penetration = value)),
                        "Jet penetration in calibers of the shell, at any speed. Early HEAT is about 2 to 4, later rounds 5 to 7.")
                    {
                        Min = 1f,
                        Max = 10f,
                        Step = 0.1f
                    },
                    readouts);
            }
            drawer.Float(set.Mass);
            drawer.Float(set.Penetration);
            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }
    }
}
