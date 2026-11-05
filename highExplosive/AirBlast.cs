extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.DamageModelling;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace HighExplosive
{
    /// <summary>The numbers behind <see cref="AirBlast"/>'s rays, apart from the game's types.</summary>
    internal static class BlastRays
    {
        /// <summary>Diameter in m of a steel fragment of <paramref name="kg"/>, as the game sizes a spall fragment.</summary>
        internal static float Diameter(float kg) => 2f * MathF.Cbrt(3f * (kg / 7850f) / (4f * MathF.PI));

        /// <summary>
        /// The speed at which a fragment of <paramref name="kg"/> penetrates
        /// <paramref name="penetration"/> mm with penetrator constant
        /// <paramref name="k"/>: de Marre, solved for speed.
        /// </summary>
        internal static float Speed(float penetration, float kg, ushort k) =>
            k * MathF.Pow(Diameter(kg) * 10f, 0.75f) * MathF.Pow(penetration / 100f, 1f / 1.43f) / MathF.Sqrt(kg);
    }

    /// <summary>
    /// The blast of an HE or HESH shell on the outside of the armour, so it
    /// reaches plates near the one it hit, such as the hull roof under a turret
    /// hit: as the shell strikes, <see cref="Rays"/> fragments leave the point
    /// of impact in the hemisphere facing back along its path, inside the hit's
    /// own simulation, which holds the whole vehicle. A plate near the blast
    /// is met close to square on by the rays straight at it, and only at a
    /// glancing angle farther away, so distant plates shrug it off.
    /// </summary>
    /// <remarks>
    /// shortcut: a ray hits at full penetration up to <see cref="Radius"/>,
    /// then stops, as the game decides a hit before its length can be changed;
    /// upgrade to a falloff with distance if the edge shows in play.
    /// </remarks>
    internal static class AirBlast
    {
        internal static ConfigEntry<float> Rays = null!;
        internal static ConfigEntry<float> Penetration = null!;
        internal static ConfigEntry<float> HeshPenetration = null!;
        internal static ConfigEntry<float> Radius = null!;
        internal static ConfigEntry<float> Mass = null!;
        internal static ConfigEntry<float> Cone = null!;

        // Set from Start until the hit ends, or the blast has nothing to act on
        private static bool active, broken;
        private static float shellMass, penetration, radius;
        private static ushort k;
        // The rays' indices, from first up to but not including end; first is -1 until they're made
        private static int first, end;
        // Rays that reached the radius, and what came of them
        private static readonly HashSet<int> cut = new();

        internal static void Load(ConfigFile config)
        {
            Rays = config.Bind("High explosive", "Blast rays", 32f, new ConfigDescription(
                "Fragments an HE or HESH shell's blast throws back from the point of impact, so it reaches plates near the one it hit. The game makes at most 32. 0 is no blast outside the plate. Each costs frame rate.",
                new AcceptableValueRange<float>(0f, 32f), new Step(1f)));
            Penetration = config.Bind("High explosive", "Blast penetration", 0.5f, new ConfigDescription(
                "Penetration of an HE shell's blast on nearby plates, over the shell's own.",
                new AcceptableValueRange<float>(0f, 1f), new Step(0.05f)));
            HeshPenetration = config.Bind("HESH", "Blast penetration", 0.25f, new ConfigDescription(
                "Penetration of a HESH shell's blast on nearby plates, over the shell's own: lower than HE's, as its head puts its force into the plate it hit.",
                new AcceptableValueRange<float>(0f, 1f), new Step(0.05f)));
            Radius = config.Bind("High explosive", "Blast radius", 1f, new ConfigDescription(
                "How far in metres the blast of a 1 kg shell reaches. It goes as the cube root of shell mass: 8 kg is twice 1 kg.",
                new AcceptableValueRange<float>(0f, 5f), new Step(0.1f)));
            Mass = config.Bind("High explosive", "Blast mass", 0.5f, new ConfigDescription(
                "Mass of the blast's fragments, all together, over the shell's. Heavier fragments spall more where they get through.",
                new AcceptableValueRange<float>(0.05f, 1f), new Step(0.05f)));
            Cone = config.Bind("High explosive", "Blast cone", 4f, new ConfigDescription(
                "How wide the blast spreads, as the tangent of its widest angle: 4 is about 76 degrees from the shell's path. Wider puts more of it on plates beside the point of impact.",
                new AcceptableValueRange<float>(1f, 10f), new Step(0.5f)));
            Impacts.FragmentSimulating += Simulating;
            Impacts.FragmentSimulated += Simulated;
            Impacts.HitEnded += () => active = false;
        }

        /// <summary>Starts the blast of an HE or HESH hit by <paramref name="penetrator"/>, once its penetration is set.</summary>
        internal static void Start(PenetratorInfo penetrator, HeKind kind)
        {
            float scale = MathF.Cbrt(penetrator.Mass);
            shellMass = penetrator.Mass;
            k = penetrator.PenetratorConstant;
            penetration = kind.Penetration * scale * kind.Blast;
            radius = Radius.Value * scale;
            first = end = -1;
            cut.Clear();
            active = !broken && Rays.Value >= 1f && penetration > 0f && radius > 0f;
        }

        /// <summary>From <see cref="Impacts.FragmentSimulating"/>.</summary>
        private static void Simulating(PenetrationSimulation simulation, short index)
        {
            if (!active || cut.Count == 0 || simulation == null)
                return;
            try
            {
                if (cut.Contains(Fragments.Get(simulation.Pointer, index).Parent))
                {
                    FragmentArray.SetSpeed(simulation.Pointer, index, 0f);
                    cut.Add(index);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>From <see cref="Impacts.FragmentSimulated"/>.</summary>
        private static void Simulated(PenetrationSimulation simulation, short index)
        {
            if (!active || simulation == null || first >= 0 && (index < first || index >= end))
                return;
            try
            {
                IntPtr sim = simulation.Pointer;
                FragmentInfo fragment = Fragments.Get(sim, index);
                if (first >= 0)
                {
                    if (fragment.Travel > radius)
                    {
                        FragmentArray.SetTravel(sim, index, radius);
                        FragmentArray.SetSpeed(sim, index, 0f);
                        cut.Add(index);
                    }
                    return;
                }
                // The first fragment is the shell's flight to the armour; anything else is before it, or it missed
                if (!fragment.IsJet || fragment.Parent >= 0 || !float.IsFinite(fragment.Travel))
                {
                    active = false;
                    return;
                }
                int rays = (int)Rays.Value;
                float kg = shellMass * Mass.Value;
                Vector3 direction = FragmentArray.Direction(sim, index);
                // Just short of the plate, so the rays start in the air
                Vector3 point = FragmentArray.Spawn(sim, index) + direction * MathF.Max(fragment.Travel - 0.05f, 0f);
                first = simulation.FragmentCount;
                bool made = Impacts.FreshBurst(sim, point, -direction, kg, rays, BlastRays.Speed(penetration, kg / rays, k), Cone.Value, index, index);
                end = simulation.FragmentCount;
                active = made;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Fail(Exception e)
        {
            broken = true;
            active = false;
            Plugin.Logger.LogWarning($"Couldn't work out an HE blast, they stop at the plate they hit from now on: {e}");
        }
    }
}
