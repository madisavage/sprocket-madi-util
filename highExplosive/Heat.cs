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
    /// wide, <see cref="JetMass"/> of the shell's mass, penetrating what its
    /// <see cref="ShapedCharge"/> design gives whatever the speed. The
    /// game works out spall for the jet, but it meets every plate by
    /// line of sight and can't ricochet (see <see cref="Impacts.LineOfSight"/>);
    /// each plate takes its line-of-sight thickness off the jet's penetration,
    /// and each gap after a plate <see cref="AirLoss"/> (see <see cref="JetBudget"/>).
    /// </summary>
    internal static class Heat
    {
        internal static ConfigEntry<float> PeakPenetration = null!;
        internal static ConfigEntry<float> StandoffDrag = null!;
        internal static ConfigEntry<float> StandoffMass = null!;
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
            PeakPenetration = config.Bind("HEAT", "Peak penetration", 4.5f, new ConfigDescription(
                "Penetration in calibers of a HEAT round at its best standoff with a 60 degree cone. Narrower cones go up to 1.4 times this. Lower it for early-war play: early HEAT is about 2 to 4, later rounds 5 to 7.",
                new AcceptableValueRange<float>(1f, 10f), new Step(0.1f)));
            StandoffDrag = config.Bind("HEAT", "Standoff drag", 0.2f, new ConfigDescription(
                "Drag a HEAT round's nose adds per caliber of standoff past the first, over the game's shell: at 0.2, 2.5 calibers is 1.3 times the drag.",
                new AcceptableValueRange<float>(0f, 1f), new Step(0.05f)));
            StandoffDrag.SettingChanged += (_, _) => Loadout.Invalidate();
            StandoffMass = config.Bind("HEAT", "Standoff mass", 0.2f, new ConfigDescription(
                "Mass a HEAT round's nose adds per caliber of standoff past the first, over the shell's, which lowers its muzzle velocity: at 0.2, 2.5 calibers is 1.3 times the mass. Penetration doesn't change.",
                new AcceptableValueRange<float>(0f, 1f), new Step(0.05f)));
            StandoffMass.SettingChanged += (_, _) => Loadout.Invalidate();
            JetDiameter = config.Bind("HEAT", "Jet diameter", 0.2f, new ConfigDescription(
                "Width of a HEAT round's jet as it hits with a 60 degree cone, over the caliber. Narrower jets spall in a tighter cone.",
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
            SettingsTab.AddSection("Rounds", "HEAT", PeakPenetration, StandoffDrag, StandoffMass, JetDiameter, JetMass, FirstSpall, AirLoss, BreakupFragments, BreakupMass, BreakupCone, LogHits);
            Impacts.FragmentSimulating += JetBudget.Simulating;
            Impacts.FragmentSimulated += JetBudget.Simulated;
            Impacts.HitSimulated += JetLog.Simulated;
            using (var icon = Plugin.Icon(HeatRound.Type))
                Loadout.Register(HeatRound.Type, "HEAT", HeatEditor.Draw, (game, cannon, id, functions) =>
                {
                    if (cannon.Blueprint is not { } owner)
                        return null;
                    HeatRound round = Round(owner, id);
                    RoundShape shape = Plugin.Shell(game, functions, round.Mass);
                    shape.Drag *= ShapedCharge.Nose(round.Standoff, StandoffDrag.Value);
                    // After Shell, which scales the explosive by its mass: the nose is empty
                    float nose = ShapedCharge.Nose(round.Standoff, StandoffMass.Value);
                    shape.Mass *= nose;
                    shape.Length *= nose;
                    shape.LaunchLength *= nose;
                    return shape;
                }, Hit, icon, penetration: (owner, id, _) => Calibers(Round(owner, id)) * owner.Caliber);
        }

        internal static HeatRound Round(CannonBlueprint owner, string id)
        {
            HeatRound round = Loadout.Get<HeatRound>(owner, id, HeatRound.Type) ?? new HeatRound();
            round.Upgrade();
            return round;
        }

        /// <summary><paramref name="round"/>'s mass with its nose, over the game's full-bore shell's.</summary>
        internal static float ShellMass(HeatRound round) => round.Mass * ShapedCharge.Nose(round.Standoff, StandoffMass.Value);

        /// <summary><paramref name="round"/>'s penetration in calibers of its shell.</summary>
        internal static float Calibers(HeatRound round) => ShapedCharge.Penetration(round.Standoff, round.Cone, PeakPenetration.Value);

        /// <summary>A HEAT hit: the shell's penetrator becomes its jet, with the round's penetration in calibers at any speed.</summary>
        private static void Hit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id)
        {
            HeatRound round = Round(owner, id);
            Jet(ref penetrator, Calibers(round) * penetrator.Diameter * 1000f, round.Cone);
        }

        /// <summary>
        /// Makes <paramref name="penetrator"/>, a HEAT shell, its jet,
        /// penetrating <paramref name="penetration"/> mm, as wide as a liner of
        /// <paramref name="cone"/> degrees makes it, with
        /// <see cref="FirstSpall"/> on the first plate it perforates.
        /// </summary>
        internal static void Jet(ref PenetratorInfo penetrator, float penetration, float cone)
        {
            Impacts.FirstBurst = Plugin.Split(FirstSpall.Value);
            Impacts.LineOfSight = true;
            PenetratorInfo p = penetrator;
            penetrator = new PenetratorInfo(p.Mass * JetMass.Value, p.Diameter * MathF.Min(JetDiameter.Value * ShapedCharge.WidthFactor(cone), 1f), p.Density, p.MaxNormalizationAngle, p.PenetratorConstant, p.Position, p.Velocity);
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

        /// <summary>Nose length in calibers; see <see cref="ShapedCharge"/>.</summary>
        public float Standoff { get; set; } = ShapedCharge.BestStandoff;

        /// <summary>The liner's full angle in degrees; see <see cref="ShapedCharge"/>.</summary>
        public float Cone { get; set; } = ShapedCharge.DefaultCone;

        /// <summary>Penetration in calibers an older round was saved with, before <see cref="Upgrade"/>; null for any other.</summary>
        public float? Penetration { get; set; }

        /// <summary>Gives an older round the design closest to its saved penetration.</summary>
        internal void Upgrade()
        {
            if (Penetration is not { } saved)
                return;
            (Standoff, Cone) = ShapedCharge.Fit(saved, Heat.PeakPenetration.Value);
            Penetration = null;
        }
    }

    /// <summary>Draws a HEAT round's shell mass, standoff, cone and performance in the cannon panel's <b>Rounds</b> section.</summary>
    internal static class HeatEditor
    {
        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            if (cannon.Blueprint is not { } gun)
                return;
            RoundFields.Draw(drawer, RoundFields.For(gun, id, HeatRound.Type, () =>
            {
                const string description = "Penetration is the jet's, the same at any range.";
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Muzzle velocity", () => $"{Editor.Velocity(gun, Heat.ShellMass(Heat.Round(gun, id))):0} m/s", description),
                    EditorGui.Readout("Penetration", () => $"{Heat.Calibers(Heat.Round(gun, id)) * gun.Caliber:0} mm", description),
                    EditorGui.Readout("Shell weight", () => $"{ShellProperties.GetProjectileMass(gun.Caliber) * Heat.ShellMass(Heat.Round(gun, id)):0.00} kg", description),
                    EditorGui.Readout("Drag", () => $"{ShapedCharge.Nose(Heat.Round(gun, id).Standoff, Heat.StandoffDrag.Value):0.00} x the shell's", description),
                    EditorGui.Readout("Spall", () => $"{MathF.Pow(ShapedCharge.WidthFactor(Heat.Round(gun, id).Cone), 3f):0.00} x a 60 degree cone's", description),
                };
                // Edit keeps fields it isn't given, so an older round's design is fixed before its first change
                FloatProperty Field(string name, Func<HeatRound, float> get, Action<HeatRound, float> set, string tooltip, float min, float max, float step) =>
                    RoundFields.Field<HeatRound>(gun, id, name, () => get(Heat.Round(gun, id)), (r, value) => { r.Upgrade(); set(r, value); }, tooltip, changed, readouts, min, step, max);
                return (new[]
                {
                    Field("Shell mass", r => r.Mass, (r, v) => r.Mass = v,
                        "Shell mass over the game's full-bore shell for this gun. Heavier shells leave slower; penetration doesn't change.", 0.25f, 4f, 0.05f),
                    Field("Standoff", r => r.Standoff, (r, v) => r.Standoff = v,
                        "Nose length in calibers, the distance the jet has to form. Penetration peaks at 2.5: too short and the jet hasn't formed, too long and it has broken up. A longer nose adds mass, so the shell leaves slower, and drag.",
                        ShapedCharge.MinStandoff, ShapedCharge.MaxStandoff, 0.1f),
                    Field("Cone angle", r => r.Cone, (r, v) => r.Cone = v,
                        "Full angle of the charge's liner in degrees. Narrow makes a deep, thin jet with little spall; wide makes a shallower, fatter jet with much more.",
                        ShapedCharge.MinCone, ShapedCharge.MaxCone, 5f),
                }, readouts);
            }));
        }
    }
}
