extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace HighExplosive
{
    /// <summary>
    /// The APHE round type: the game's full-bore AP shell carrying
    /// <see cref="ApheRound.Filler"/> kg of explosive. It penetrates
    /// as the game's shell, less <see cref="PenetrationLoss"/> for its
    /// filler; once it has gone <see cref="ApheRound.Fuze"/> past where it
    /// first entered a plate it bursts, its casing thrown
    /// forward and back as spall (see <see cref="Fuze"/>). More filler
    /// breaks the casing into more, lighter fragments, thrown faster.
    /// </summary>
    internal static class Aphe
    {
        internal static ConfigEntry<float> Fragmentation = null!;
        internal static ConfigEntry<float> MaxFragments = null!;
        internal static ConfigEntry<float> Spread = null!;
        internal static ConfigEntry<float> Gurney = null!;
        internal static ConfigEntry<float> FragmentMass = null!;
        internal static ConfigEntry<float> PenetrationLoss = null!;
        internal static ConfigEntry<bool> LogBursts = null!;
        internal static ManualLogSource Log = null!;

        internal static void Load(ConfigFile config, ManualLogSource log)
        {
            Log = log;
            Fragmentation = config.Bind("APHE", "Fragmentation", 4f, new ConfigDescription(
                "Fragments each way per percent of filler when an APHE shell bursts: the casing's mass is split between them, so more filler means more, lighter fragments.",
                new AcceptableValueRange<float>(0.5f, 20f), new Step(0.5f)));
            MaxFragments = config.Bind("APHE", "Max fragments", 32f, new ConfigDescription(
                "Most fragments each way an APHE shell bursts into; past it fragments get heavier, not more. Each costs frame rate.",
                new AcceptableValueRange<float>(1f, 128f), new Step(1f)));
            Spread = config.Bind("APHE", "Burst cone", 3f, new ConfigDescription(
                "Width of an APHE shell's burst cones, times the cone of the plate spall it is copied from.",
                new AcceptableValueRange<float>(0.5f, 10f), new Step(0.25f)));
            Gurney = config.Bind("APHE", "Explosive speed", 2440f, new ConfigDescription(
                "The filler's Gurney constant in m/s, which sets fragment speed with the filler-to-casing ratio. TNT is about 2440.",
                new AcceptableValueRange<float>(500f, 5000f), new Step(20f)));
            FragmentMass = config.Bind("APHE", "Fragment mass", 1f, new ConfigDescription(
                "Mass of an APHE shell's burst fragments, times its casing's. 1 is the whole casing.",
                new AcceptableValueRange<float>(0f, 4f), new Step(0.05f)));
            PenetrationLoss = config.Bind("APHE", "Penetration loss", 3f, new ConfigDescription(
                "Percent of the game's penetration an APHE shell loses per percent of filler, as the cavity weakens it: at 3, 1.5% filler loses 4.5%. 0 loses none.",
                new AcceptableValueRange<float>(0f, 10f), new Step(0.25f)));
            LogBursts = config.Bind("APHE", "Log bursts", false,
                "Logs each APHE shell's path to its burst, the burst, and what each fragment made after it did: its flags, material, distance and speed.");
            SettingsTab.AddSection("APHE", Fragmentation, MaxFragments, Spread, Gurney, FragmentMass, PenetrationLoss, LogBursts);
            using (var icon = Plugin.Icon(ApheRound.Type))
                Loadout.Register(ApheRound.Type, "APHE", ApheEditor.Draw, (game, cannon, id, functions) =>
                    cannon.Blueprint is { } owner ? Plugin.Shell(game, functions, 1f, explode: false) : null, Hit, icon);
            Impacts.HitEnded += Fuze.End;
            Impacts.FragmentSimulating += Fuze.Simulating;
            Impacts.FragmentSimulated += Fuze.Simulated;
        }

        internal static ApheRound Round(CannonBlueprint owner, string id) => Loadout.Get<ApheRound>(owner, id, ApheRound.Type) ?? new ApheRound();

        private static void Hit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id) => Shoot(ref penetrator, Round(owner, id));

        /// <summary>An APHE hit for <paramref name="round"/>: the game's penetration less its filler's, then its fuze.</summary>
        internal static void Shoot(ref PenetratorInfo penetrator, ApheRound round)
        {
            float share = Share(round.Filler, penetrator.Mass);
            Impacts.SetPenetration(ref penetrator, Impacts.Penetration(penetrator) * Penetration(share));
            Fuze.Start(round, penetrator.Mass, share);
        }

        /// <summary>Most of a shell's mass its filler can be.</summary>
        internal const float MaxShare = 0.1f;

        /// <summary>The share of a <paramref name="shell"/> kg shell's mass that <paramref name="filler"/> kg of explosive is; at most <see cref="MaxShare"/>.</summary>
        internal static float Share(float filler, float shell) => Math.Clamp(filler / MathF.Max(shell, 1e-3f), 0f, MaxShare);

        /// <summary>The share of the game's penetration a shell with <paramref name="filler"/> of its mass in explosive keeps.</summary>
        internal static float Penetration(float filler) => Math.Max(1f - filler * PenetrationLoss.Value, 0.05f);

        /// <summary>
        /// Fragment speed in m/s of a cylinder of casing <paramref name="filler"/>
        /// of whose mass is explosive: Gurney, <c>√2E / √(M/C + 1/2)</c>.
        /// </summary>
        internal static float FragmentSpeed(float filler) => Gurney.Value / MathF.Sqrt((1f - filler) / MathF.Max(filler, 1e-4f) + 0.5f);

        /// <summary>Fragments each way for <paramref name="filler"/> of the shell's mass in explosive.</summary>
        internal static int Fragments(float filler) => Math.Clamp((int)MathF.Round(filler * 100f * Fragmentation.Value), 1, (int)MaxFragments.Value);
    }

    /// <summary>An APHE round, as stored in the vehicle file through <see cref="Loadout"/>.</summary>
    internal sealed class ApheRound
    {
        /// <summary>Its round type in <see cref="Loadout"/>.</summary>
        internal const string Type = "aphe";

        /// <summary>Explosive in kg; see <see cref="Aphe.Share"/>.</summary>
        public float Filler { get; set; } = 0.1f;

        /// <summary>Distance in m the shell goes past where it first enters a plate before it bursts.</summary>
        public float Fuze { get; set; } = 0.5f;
    }

    /// <summary>
    /// Bursts an APHE shell once its path from the first plate it enters is
    /// <see cref="ApheRound.Fuze"/> long: the shell stops there, nothing
    /// that comes of its piece of path past it happens, and the casing goes
    /// forward and back as <see cref="Aphe.Fragments"/> fragments each way,
    /// copied from the hit's own plate spall. An HE hit (see
    /// <see cref="StartAtExit"/>) instead stops as it leaves the first plate,
    /// with no burst: the plate's spall is all that gets in.
    /// </summary>
    /// <remarks>
    /// The shell's path is fragments flagged as the original penetrator,
    /// simulated in path order, each knowing its length once simulated; the
    /// burst is placed on the one the fuze runs out in, after it. A shell
    /// that stops before the fuze runs out, or leaves the vehicle, doesn't
    /// burst inside it. The game simulates fragments in index order, so the
    /// bursts' fragments, and anything the cut piece made, come after.
    /// </remarks>
    internal static class Fuze
    {
        private static ApheRound? round;
        // Set from Start until the hit ends
        private static bool active;
        private static float shellMass, share, travelled;
        private static bool armed, burst, broken;
        // Set for an HE hit
        private static bool atExit;
        // This hit's log while Log bursts is on, written at its end; fragments from the first made at the burst are logged
        private static System.Text.StringBuilder? log;
        private static int firstMade, logged;
        // Fragments cut by the burst; what comes of them is cut too
        private static readonly HashSet<int> cut = new();

        /// <summary>Starts an HE hit: the shell goes no further than the far face of the first plate it gets through.</summary>
        internal static void StartAtExit()
        {
            Start(null, 0f, 0f);
            atExit = true;
        }

        internal static void Start(ApheRound? r, float mass, float filler)
        {
            round = r;
            atExit = false;
            shellMass = mass;
            share = filler;
            travelled = 0f;
            armed = burst = false;
            cut.Clear();
            log = r != null && Aphe.LogBursts.Value ? new System.Text.StringBuilder($"APHE hit: shell {mass:0.00} kg, filler {r.Filler * 1000f:0} g ({filler * 100f:0.#}%), fuze {r.Fuze:0.00} m") : null;
            active = true;
            firstMade = int.MaxValue;
            logged = 0;
        }

        internal static void End()
        {
            if (active && log != null)
                Aphe.Log.LogInfo(log.Append(burst ? "" : "\n  no burst: the shell stopped or left first").ToString());
            active = atExit = false;
            round = null;
            log = null;
        }

        /// <summary>From <see cref="Impacts.FragmentSimulating"/>.</summary>
        internal static void Simulating(PenetrationSimulation __0, short __1)
        {
            if (!active || cut.Count == 0 || __0 == null)
                return;
            try
            {
                if (Fragments.Get(__0.Pointer, __1).Parent is var parent and >= 0 && cut.Contains(parent))
                {
                    FragmentArray.SetSpeed(__0.Pointer, __1, 0f);
                    cut.Add(__1);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static string Describe(IntPtr simulation, int index, FragmentInfo f) =>
            $"#{index} (parent {f.Parent}, material {f.Material}, raw flags 0x{FragmentArray.RawFlags(simulation, index):x}): {f.Flags}, {f.Travel * 1000f:0} mm at {f.Speed:0} m/s, {f.Mass * 1000f:0.#} g"
            + (cut.Contains(index) ? ", cut" : "");

        /// <summary>From <see cref="Impacts.FragmentSimulated"/>.</summary>
        internal static void Simulated(PenetrationSimulation __0, short __1)
        {
            if (!active || broken || __0 == null)
                return;
            try
            {
                IntPtr simulation = __0.Pointer;
                if (burst)
                {
                    if (log != null && __1 >= firstMade && logged++ < 200)
                        log.Append("\n    ").Append(Describe(simulation, __1, Fragments.Get(simulation, __1)));
                    return;
                }
                FragmentInfo shell = Fragments.Get(simulation, __1);
                if (!shell.IsJet)
                    return;
                bool inPlate = (shell.Flags & PenetrationFragmentFlag.InMaterial) != 0;
                armed |= inPlate;
                if (atExit)
                {
                    // Out of the first plate: the shell goes no further, what comes of this gap with it
                    if (armed && !inPlate)
                    {
                        burst = true;
                        FragmentArray.SetTravel(simulation, __1, 0f);
                        FragmentArray.SetSpeed(simulation, __1, 0f);
                        cut.Add(__1);
                    }
                    return;
                }
                log?.Append("\n  shell ").Append(Describe(simulation, __1, shell)).Append(armed ? $", {travelled * 1000f:0} mm past the first plate" : ", not armed");
                if (!armed)
                    return;
                float before = travelled;
                travelled += shell.Travel;
                if (travelled < round!.Fuze)
                    return;

                burst = true;
                float along = round.Fuze - before;
                Vector3 direction = FragmentArray.Direction(simulation, __1);
                Vector3 point = FragmentArray.Spawn(simulation, __1) + direction * along;
                // The shell stops at the burst, and what its piece of path made past it with it
                FragmentArray.SetTravel(simulation, __1, along);
                FragmentArray.SetSpeed(simulation, __1, 0f);
                cut.Add(__1);

                float filler = share;
                int fragments = Aphe.Fragments(filler);
                // The game makes at most 32 fragments a burst
                int bursts = (fragments + 31) / 32;
                float speed = Aphe.FragmentSpeed(filler);
                float kg = shellMass * (1f - filler) * Aphe.FragmentMass.Value / (2 * bursts);
                // Its parent isn't cut, so the bursts' fragments aren't
                short parent = shell.Parent;
                firstMade = __0.FragmentCount;
                bool made = true;
                for (int i = 0; i < bursts && made; i++)
                {
                    // Spread the count over the bursts: 40 is 20 and 20
                    int count = (fragments * (i + 1) / bursts) - (fragments * i / bursts);
                    made = Impacts.Burst(simulation, point, direction, kg, count, speed, Aphe.Spread.Value, parent, __1)
                        && Impacts.Burst(simulation, point, -direction, kg, count, speed, Aphe.Spread.Value, parent, __1);
                }
                log?.Append($"\n  burst {along * 1000f:0} mm into #{__1} at {point}, {bursts} burst(s) each way of {fragments} fragments, {kg * 1000f:0} g a burst, {speed:0} m/s: ")
                    .Append(made ? $"made #{firstMade} to #{__0.FragmentCount - 1}"
                        : $"not all made (made up to #{__0.FragmentCount - 1}): no plate spall to copy, or #{__1} is out of material and not precomputed")
                    .Append("\n  after the burst:");
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Fail(Exception e)
        {
            broken = true;
            Aphe.Log.LogWarning($"Couldn't burst an APHE shell, they hit as solid shot from now on: {e}");
        }
    }

    /// <summary>Draws an APHE round's filler and fuze, and its performance, in the cannon panel's <b>Rounds</b> section.</summary>
    internal static class ApheEditor
    {
        // Cannon blueprint and round id, (Filler, Fuze, Readouts); one entry for each APHE round
        private static readonly Dictionary<(IntPtr, string), (FloatProperty Filler, FloatProperty Fuze, StringProperty[] Readouts)> properties = new();

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            if (!properties.TryGetValue((gun.Pointer, id), out var set))
            {
                ApheRound R() => Aphe.Round(gun, id);
                float Kg() => ShellProperties.GetProjectileMass(gun.Caliber);
                float Share() => Aphe.Share(R().Filler, Kg());
                const string description = "Penetration is the game's for the shell's mass and speed, less the filler's loss. The casing bursts into spall forward and back.";
                StringProperty[] readouts =
                {
                    EditorGui.Readout("Muzzle velocity", () => $"{gun.MuzzleVelocity:0} m/s", description),
                    EditorGui.Readout("Shell weight", () => $"{Kg():0.00} kg", description),
                    EditorGui.Readout("Filler share", () => $"{Share() * 100f:0.#}% of the shell", description),
                    EditorGui.Readout("Penetration loss", () => $"{(1f - Aphe.Penetration(Share())) * 100f:0.#}%", description),
                    EditorGui.Readout("Fragments", () => $"{2 * Aphe.Fragments(Share())} at {Aphe.FragmentSpeed(Share()):0} m/s", description),
                };
                void Set(Action<ApheRound> change)
                {
                    Loadout.Edit(gun, id, change);
                    changed();
                    EditorGui.Refresh(readouts);
                }
                properties[(gun.Pointer, id)] = set = (
                    new FloatProperty(
                        "Filler (g)",
                        (Func<float>)(() => R().Filler * 1000f),
                        (Action<float>)(value => Set(r => r.Filler = value / 1000f)),
                        "Explosive in grams; at most 10% of the shell. More breaks the casing into more, lighter, faster fragments, but costs penetration. Wartime APHE is about 0.3 to 3% of the shell.")
                    {
                        Min = 5f,
                        Max = 2000f,
                        Step = 5f
                    },
                    new FloatProperty(
                        "Fuze delay",
                        (Func<float>)(() => R().Fuze),
                        (Action<float>)(value => Set(r => r.Fuze = value)),
                        "Metres the shell goes past where it first enters a plate before it bursts. A shell that stops or leaves the vehicle sooner doesn't burst inside it.")
                    {
                        Min = 0.05f,
                        Max = 3f,
                        Step = 0.05f
                    },
                    readouts);
            }
            // Grams of 10% of the shell, which changes with the gun's caliber
            set.Filler.Max = MathF.Max(ShellProperties.GetProjectileMass(gun.Caliber) * Aphe.MaxShare * 1000f, set.Filler.Min + set.Filler.Step);
            drawer.Float(set.Filler);
            drawer.Float(set.Fuze);
            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }
    }
}
