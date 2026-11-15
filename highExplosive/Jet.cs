extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Rounds;
using Sprocket.DamageModelling;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace HighExplosive
{
    /// <summary>
    /// A HEAT jet's penetration as a budget: each plate takes its
    /// line-of-sight thickness (times its material's RHA factor) off it, and
    /// each gap after a plate <see cref="Heat.AirLoss"/> per metre. The
    /// game's own residual speed is for a solid shot, which keeps far more
    /// after a plate. Once the budget runs out the jet stops: in a plate,
    /// with no spall out of its back; in a gap, where it ran out, breaking up
    /// there into <see cref="Heat.BreakupFragments"/> weak fragments going on.
    /// </summary>
    /// <remarks>
    /// The game works out a penetrator's speed after a plate inline, so it
    /// can't be patched; instead each fragment is changed just before the
    /// game simulates it. The game simulates fragments once each in index
    /// order, after the fragment they came from, so by then the plate or
    /// gap behind a fragment is done and its length known.
    /// <para>
    /// The budget is carried in the jet's mass, at the speed it hit with:
    /// the game's penetration goes as <c>m^0.715</c> at a fixed speed, and
    /// a jet slowed instead ricochets off the next surface like a spent shell.
    /// Its energy, and so its damage and spall, shrinks with it.
    /// </para>
    /// <para>
    /// The game decides whether the jet perforates a plate before any of
    /// this, so a jet out of budget in a plate the game lets it through is
    /// stopped behind it, and the plate's spall with it. Fields are written
    /// into the simulation's own fragment array, as <c>Fragment</c> is a
    /// struct Il2CppInterop can't write.
    /// </para>
    /// </remarks>
    internal static class JetBudget
    {
        // Set from Heat.Jet until its hit ends
        private static bool active;
        private static ushort k;
        // Spall fragments stopped this hit, for the log
        internal static int Stopped;
        // Fragments stopped this hit; what comes of them is stopped too
        private static readonly HashSet<int> stopped = new();
        // Set once the jet is stopped this hit: a ricochet's jet fragment can name a spall fragment as its parent
        private static bool jetStopped;
        // Gap index, the NERA loss (mm) at its end; worked out once per gap this hit
        private static readonly Dictionary<int, float> nera = new();

        internal static void Start(ushort penetratorConstant)
        {
            active = true;
            k = penetratorConstant;
            Stopped = 0;
            stopped.Clear();
            jetStopped = false;
            nera.Clear();
        }

        internal static void End() => active = false;

        private static void Stop(IntPtr simulation, int index, bool jet)
        {
            FragmentArray.SetSpeed(simulation, index, 0f);
            stopped.Add(index);
            jetStopped |= jet;
        }

        /// <summary>
        /// The jet's budget arriving at the end of gap <paramref name="index"/>: its penetration, less the gap's air loss if it left
        /// a plate, less <see cref="Nera"/>'s.
        /// </summary>
        private static float Arriving(PenetrationSimulation simulation, int index, FragmentInfo gap) =>
            // The first gap is the shell's flight to the vehicle; any after it left a plate
            gap.Penetration(k) - (gap.Parent >= 0 ? Heat.AirLoss.Value * gap.Travel + Nera(simulation, index, gap) : 0f);

        /// <summary>
        /// What NERA takes off the jet by the end of gap <paramref name="index"/>: <see cref="Rounds.Nera.Loss"/> × <see cref="Heat.NeraStrength"/>.
        /// </summary>
        private static float Nera(PenetrationSimulation simulation, int index, FragmentInfo gap)
        {
            if (Heat.NeraStrength.Value <= 0f)
                return 0f;
            if (!nera.TryGetValue(index, out float loss))
                nera[index] = loss = Heat.NeraStrength.Value * Rounds.Nera.Loss(simulation, gap.Parent, gap.Travel, Heat.LogHits.Value ? line => Heat.Log.LogInfo("HEAT " + line) : null);
            return loss;
        }

        /// <summary>From <see cref="Impacts.FragmentSimulating"/>.</summary>
        internal static void Simulating(PenetrationSimulation __0, short __1)
        {
            if (!active || __0 == null)
                return;
            try
            {
                IntPtr simulation = __0.Pointer;
                FragmentInfo fragment = Fragments.Get(simulation, __1);
                if (fragment.Parent < 0)
                    return;
                if (stopped.Contains(fragment.Parent) || jetStopped && fragment.IsJet)
                {
                    Stop(simulation, __1, fragment.IsJet);
                    return;
                }
                FragmentInfo parent = Fragments.Get(simulation, fragment.Parent);
                if (!parent.IsJet || parent.Parent < 0)
                    return;
                bool inPlate = (parent.Flags & PenetrationFragmentFlag.InMaterial) != 0;
                // After a gap: a gap that used up the budget was cut, and what comes of it stopped, by Postfix
                if (!inPlate)
                    return;
                if (parent.Material < 0)
                    return;

                // Out of a plate: the jet, or the plate's spall
                FragmentInfo gap = Fragments.Get(simulation, parent.Parent);
                float budget = gap.Penetration(k);
                if (budget <= 0f)
                    return;
                float arriving = Arriving(__0, parent.Parent, gap);
                float rha = __0.materials[parent.Material].rhaFactor;
                float taken = parent.Travel * 1000f * rha;
                float left = arriving - taken;
                if (!fragment.IsJet)
                {
                    if (left <= 0f)
                    {
                        Stop(simulation, __1, false);
                        Stopped++;
                    }
                    return;
                }
                if (left <= 0f)
                    Stop(simulation, __1, true);
                else
                {
                    // Penetration goes as m^0.715 at a fixed speed and penetrator constant
                    FragmentArray.SetMass(simulation, __1, gap.Mass * MathF.Pow(left / budget, 1f / 0.715f));
                    FragmentArray.SetSpeed(simulation, __1, gap.Speed);
                }
                if (Heat.LogHits.Value)
                    Heat.Log.LogInfo($"HEAT plate #{fragment.Parent} (material {parent.Material}, RHA x{rha:0.##}):{budget:0} mm at {gap.Speed:0} m/s, gap {gap.Travel * 1000f:0} mm takes {budget - arriving:0} mm, "
                        + $"plate {parent.Travel * 1000f:0} mm takes {taken:0} mm -> {Math.Max(left, 0f):0} mm left, #{__1} "
                        + (left > 0f ? $"leaves at {gap.Speed:0} m/s, {gap.Mass * MathF.Pow(left / budget, 1f / 0.715f):0.000} kg" : "stopped")
                        + $" (game: {fragment.Speed:0} m/s)");
            }
            catch (Exception e)
            {
                Heat.Log.LogWarning($"Couldn't set the HEAT jet's budget, the game's stands: {e}");
                End();
            }
        }

        /// <summary>
        /// Breaks the jet up where a gap after a plate uses up its budget: the
        /// gap is cut there, what it would have hit doesn't happen, and the
        /// jet goes on as <see cref="Heat.BreakupFragments"/> fragments of
        /// <see cref="Heat.BreakupMass"/> of its mass, at its speed.
        /// </summary>
        internal static void Simulated(PenetrationSimulation __0, short __1)
        {
            if (!active || jetStopped || __0 == null)
                return;
            try
            {
                IntPtr simulation = __0.Pointer;
                FragmentInfo gap = Fragments.Get(simulation, __1);
                // The first gap is the shell's flight to the vehicle
                if (!gap.IsJet)
                    return;
                if (gap.Parent < 0)
                    return;
                if ((gap.Flags & PenetrationFragmentFlag.InMaterial) != 0)
                    return;
                if (Arriving(__0, __1, gap) > 0f)
                    return;
                float budget = gap.Penetration(k);
                // NERA bulges in at the start of the gap; the air takes the rest
                float left = budget - Nera(__0, __1, gap);
                float along = Heat.AirLoss.Value > 0f ? Math.Clamp(left / Heat.AirLoss.Value, 0f, gap.Travel) : 0f;
                Vector3 direction = FragmentArray.Direction(simulation, __1);
                Vector3 point = FragmentArray.Spawn(simulation, __1) + direction * along;
                FragmentArray.SetTravel(simulation, __1, along);
                Stop(simulation, __1, true);
                int count = (int)Heat.BreakupFragments.Value;
                int first = __0.FragmentCount;
                // Its parent is the plate the jet left, not stopped, so the fragments aren't
                bool made = count > 0 && Impacts.Burst(simulation, point, direction, gap.Mass * Heat.BreakupMass.Value, count, gap.Speed, Heat.BreakupCone.Value, gap.Parent, __1);
                if (Heat.LogHits.Value)
                {
                    string outcome;
                    if (made)
                        outcome = $"broke up into #{first} to #{__0.FragmentCount - 1}, {gap.Mass * Heat.BreakupMass.Value * 1000f / count:0.#} g each at {gap.Speed:0} m/s";
                    else if (count > 0)
                        outcome = "couldn't break up: no plate spall to copy, or the gap isn't made as the game makes one";
                    else
                        outcome = "stopped";
                    Heat.Log.LogInfo($"HEAT gap #{__1}: {budget:0} mm used up {along * 1000f:0} mm in, of {gap.Travel * 1000f:0} mm; " + outcome);
                }
            }
            catch (Exception e)
            {
                Heat.Log.LogWarning($"Couldn't break up the HEAT jet, it stops where it lands: {e}");
                End();
            }
        }
    }

    /// <summary>
    /// Ends each HEAT hit, and when <see cref="Heat.LogHits"/> is on, logs
    /// the jet's path through the armour: each step with its flags, distance
    /// travelled, speed and the penetration left at that speed, and how many
    /// fragments (spall) the hit made.
    /// </summary>
    internal static class JetLog
    {
        // Set by Heat.Jet, used up by the innermost entry point, which returns first
        internal static bool Pending;

        private static bool broken;

        /// <summary>From <see cref="Impacts.HitSimulated"/>.</summary>
        internal static void Simulated(PenetratorInfo __0, IPenetrationSimulation? __result)
        {
            if (!Pending || __result?.TryCast<PenetrationSimulation>() is not { } simulation)
            {
                JetBudget.End();
                return;
            }
            Pending = false;
            JetBudget.End();
            if (broken)
                return;
            try
            {
                ushort k = __0.PenetratorConstant;
                var log = new System.Text.StringBuilder($"HEAT path: hit {simulation.Hit}, {simulation.FragmentCount} fragments");
                int spall = 0;
                for (int i = 0; i < simulation.FragmentCount; i++)
                {
                    FragmentInfo f = Fragments.Get(simulation.Pointer, i);
                    if (!f.IsJet)
                    {
                        spall++;
                        continue;
                    }
                    if (f.Parent >= 0)
                    {
                        // A parent that isn't jet is logged too
                        FragmentInfo p = Fragments.Get(simulation.Pointer, f.Parent);
                        if (!p.IsJet)
                            log.Append($"\n  #{f.Parent} (not jet; parent {p.Parent}, material {p.Material}): {p.Flags}, travelled {p.Travel * 1000f:0} mm, {Step(p, k)}");
                    }
                    log.Append($"\n  #{i} (parent {f.Parent}, material {f.Material}): {f.Flags}, travelled {f.Travel * 1000f:0} mm, {Step(f, k)}");
                }
                FragmentInfo final = Fragments.Final(simulation.Pointer);
                log.Append($"\n  final: {final.Flags}, {Step(final, k)}; {spall} spall fragments, {JetBudget.Stopped} stopped behind plates the jet ran out in");
                Heat.Log.LogInfo(log.ToString());
            }
            catch (Exception e)
            {
                broken = true;
                Heat.Log.LogWarning($"Couldn't read the HEAT jet's path, not logging it again: {e}");
            }
        }

        private static string Step(FragmentInfo f, ushort k) => $"{f.Speed:0} m/s, {f.Mass:0.000} kg {f.Diameter * 1000f:0.0} mm, {f.Penetration(k):0} mm left";
    }
}
