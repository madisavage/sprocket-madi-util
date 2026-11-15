extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using MadiLib;
using Sprocket.DamageModelling;
using N = System.Numerics.Vector3;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace Rounds
{
    /// <summary>
    /// Non-explosive reactive armour: a plate whose material file has <c>"nera": mm</c> in its properties
    /// is an interlayer that swells as a penetrator goes through, pushing its flyer plates across the
    /// penetrator's path. Each penetrator's plugin scales <see cref="Loss"/> for itself (a HEAT jet loses
    /// it all; a long rod a little).
    /// </summary>
    public static class Nera
    {
        // A gap shorter than this between plates is contact: an interlayer and its back flyer plate
        private const float Contact = 0.002f;
        // Interlayer thickness (m, normal to the plate) and room behind (m) at which NERA has its full effect; thinner or closer takes less, in proportion
        private const float Interlayer = 0.010f, Room = 0.030f;

        /// <summary>Materials with <c>"nera"</c>, as the penetration sim knows them: name, and the most it takes off a HEAT jet (mm).</summary>
        public static readonly Dictionary<(float Spall, float Rha, float Density), (string Name, float Value)> Materials = TechFiles.Flagged(TechFiles.Folder, "nera");

        // The hit's armour in sim space, read at the first NERA plate; cleared as each hit ends
        private static (N[] Points, int[] Triangles)? armour;

        internal static void Load()
        {
            Impacts.HitEnded += () => armour = null;
            Plugin.Logger.LogInfo(Materials.Count == 0 ? "No NERA materials." : $"NERA materials: {string.Join(", ", Materials.Values.Select(n => $"{n.Name} ({n.Value:0} mm)"))}.");
        }

        private static N Num(Vector3 v) => new(v.x, v.y, v.z);

        private static (N[] Points, int[] Triangles) Armour(PenetrationSimulation simulation)
        {
            if (armour == null)
            {
                (N[] Points, int[] Triangles, int[] Objects, int Bad) read = ArmourMesh.Read(simulation);
                armour = (read.Points, read.Triangles);
            }
            return armour.Value;
        }

        /// <summary>
        /// How far fragment <paramref name="index"/>, a gap just starting, will go before it meets armour (m),
        /// from the hit's meshes: its room, before the game has simulated it.
        /// </summary>
        public static float Ahead(PenetrationSimulation simulation, int index)
        {
            var (points, triangles) = Armour(simulation);
            return ArmourMesh.Distance(points, triangles, Num(FragmentArray.Spawn(simulation.Pointer, index)), Num(FragmentArray.Direction(simulation.Pointer, index)));
        }

        /// <summary>
        /// What NERA takes (mm, as against a HEAT jet) off a penetrator leaving plate fragment
        /// <paramref name="plate"/> into a gap of <paramref name="room"/> m: for each NERA interlayer that is that plate,
        /// or the one touching it in front (the back flyer plate between), its <c>nera</c> × sin²θ (θ the penetrator's
        /// angle to the interlayer's normal; the plates bulge across it only at an angle) × its normal thickness over
        /// 10 mm × the room over 30 mm (no room, no bulge), each capped at 1. The plates either side are ordinary plates.
        /// </summary>
        /// <param name="simulation">The hit's simulation.</param>
        /// <param name="plate">The plate fragment the penetrator is leaving.</param>
        /// <param name="room">The gap behind it, in m.</param>
        /// <param name="log">Given each element's line, if any.</param>
        public static float Loss(PenetrationSimulation simulation, int plate, float room, Action<string>? log = null)
        {
            if (Materials.Count == 0 || plate < 0)
                return 0f;
            IntPtr pointer = simulation.Pointer;
            float roomFactor = Math.Min(room / Room, 1f);
            float loss = 0f;
            // No further back than the plate in front: an element pressed against thick armour has no room to bulge, whatever gap is behind the armour
            int at = plate;
            FragmentInfo fragment = Fragments.Get(pointer, at);
            for (int steps = 0; steps < 2 && (fragment.Flags & PenetrationFragmentFlag.InMaterial) != 0 && fragment.Parent >= 0; steps++)
            {
                FragmentInfo entry = Fragments.Get(pointer, fragment.Parent);
                if (fragment.Material >= 0)
                {
                    MaterialInfo m = simulation.materials[fragment.Material];
                    if (Materials.TryGetValue((m.spallFactor, m.rhaFactor, m.density), out (string Name, float Value) material))
                    {
                        // The face it entered the interlayer by: where the fragment before ended. The game doesn't record it
                        (N[] points, int[] triangles) = Armour(simulation);
                        N? face = ArmourMesh.Face(points, triangles, Num(FragmentArray.Spawn(pointer, fragment.Parent)), Num(entry.Direction), entry.Travel);
                        if (face == null)
                            break;
                        N normal = face.Value;
                        float cos = Math.Clamp(Math.Abs(N.Dot(N.Normalize(Num(entry.Direction)), normal)), 0f, 1f);
                        float taken = material.Value * (1f - cos * cos) * Math.Min(fragment.Travel * cos / Interlayer, 1f) * roomFactor;
                        loss += taken;
                        log?.Invoke($"NERA plate #{at} ({material.Name}): interlayer {fragment.Travel * cos * 1000f:0.#} mm at {MathF.Acos(cos) * 180f / MathF.PI:0} deg, "
                            + $"{room * 1000f:0} mm room behind -> {taken:0} mm");
                    }
                }
                // The stack ends at a real gap, or at the shell's flight in
                if ((entry.Flags & PenetrationFragmentFlag.InMaterial) != 0 || entry.Parent < 0 || entry.Travel >= Contact)
                    break;
                at = entry.Parent;
                fragment = Fragments.Get(pointer, at);
            }
            return loss;
        }
    }
}
