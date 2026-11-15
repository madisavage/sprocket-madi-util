extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Rounds;
using Sprocket;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using Time = UnityCoreModule::UnityEngine.Time;

namespace ShellCam
{
    /// <summary>
    /// BepInEx entry point. Shell cam: keeps the player's last few hits,
    /// each shell path and spall fragment and the armour they went through,
    /// and replays one on a keybind in a panel of its own, the lines growing
    /// outward over time over an X-ray view of the vehicle hit.
    /// </summary>
    [BepInPlugin("madi.shellcam", "shellCam", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static BepInEx.Logging.ManualLogSource Logger = null!;
        internal static ConfigEntry<float> ReplaySeconds = null!;
        internal static ConfigEntry<bool> AutoReplay = null!;
        internal static ConfigEntry<float> PanelSize = null!;

        public override void Load()
        {
            Logger = Log;
            ReplaySeconds = Config.Bind("Replay", "Replay seconds", 3f, new ConfigDescription(
                "How long a hit's shell and spall take to play out, slowed down from the milliseconds they really take.", new AcceptableValueRange<float>(0.5f, 20f), new Step(0.5f)));
            AutoReplay = Config.Bind("Replay", "Auto replay", false, "Replay each of your hits as it lands, without pressing the key.");
            PanelSize = Config.Bind("Replay", "Panel size", 360f, new ConfigDescription(
                "Width and height of the replay panel in the bottom-right corner, in pixels.", new AcceptableValueRange<float>(200f, 900f), new Step(20f)));
            SettingsTab.AddSection("Shell cam", "Shell cam", ReplaySeconds, AutoReplay, PanelSize);
            Keybinds.Register(this, "Shell cam", "Replay hit", "Alt+C", Replay.Next);
            Impacts.PlayerFired += Hits.Fired;
            Impacts.HitSimulated += Hits.Simulated;
            new Harmony("madi.shellcam").PatchAll();
            // Draws in the bottom-right corner, so it takes no room in the panel stack
            Hud.AddPanel((_, _) => { Replay.Draw(); return 0f; });
            Hud.AddPanel(Replay.Caption);
        }
    }

    /// <summary>Damage lands here, after the simulation, so around it is when a hit's health changes.</summary>
    [HarmonyPatch(typeof(PenetrationSimulation), nameof(PenetrationSimulation.ApplyFragmentHits))]
    internal static class ApplyFragmentHitsPatch
    {
        private static void Prefix(PenetrationSimulation __instance) => Hits.Applying(__instance.Pointer);

        private static void Postfix(PenetrationSimulation __instance) => Hits.Applied(__instance.Pointer);
    }

    /// <summary>A part with health among a hit's armour, with its health (0-1) just before and just after the hit's damage landed.</summary>
    internal sealed class Part
    {
        private readonly HealthPool[] pools;
        public readonly string Name;
        public float Before;
        // Null until the game applies the hit's damage
        public float? After;

        public Part(HealthPool[] pools, string name)
        {
            this.pools = pools;
            Name = name;
            Before = Health;
        }

        // Damage overflows along a part's chain of pools, so the part's health is theirs together
        public float Health
        {
            get
            {
                float current = 0f, max = 0f;
                foreach (HealthPool pool in pools)
                {
                    current += pool.Current;
                    max += pool.max;
                }
                return max > 0f ? current / max : 0f;
            }
        }
    }

    /// <summary>One fragment of a hit: its path in sim space, and when it starts and how long it flies, in seconds.</summary>
    internal readonly record struct Line(Vector3 From, Vector3 To, float Start, float Duration, float Speed, bool Shell, bool Deflected);

    /// <summary>
    /// A recorded hit, all in its simulation's space: its fragments, and the
    /// armour of the structure hit as it was at the hit (turrets turned as
    /// they were), as triangles.
    /// </summary>
    internal sealed class Hit
    {
        public readonly List<Line> Lines = new();
        public Vector3[] Points = Array.Empty<Vector3>();
        public int[] Triangles = Array.Empty<int>();
        // The parts with health, and which of them (index, or -1 for none) owns each triangle
        public readonly List<Part> Parts = new();
        public int[] Owners = Array.Empty<int>();
        // The simulation whose damage is still to land, else zero
        public IntPtr Pending;
        public float End, MaxSpeed;
        public string Outcome = "";
        // The shell's approach, from a few metres out to where it struck, and a sphere holding the vehicle and the hit
        public Vector3 ApproachFrom, Entry, Centre;
        public float Radius;

        /// <summary>Sets the approach and the bounding sphere, once lines and armour are in.</summary>
        public void Frame(float approach)
        {
            int first = Lines.FindIndex(l => l.Shell);
            Line entry = first >= 0 ? Lines[first] : Lines[0];
            Entry = entry.From;
            Vector3 path = entry.To - entry.From;
            ApproachFrom = entry.From - (path.LengthSquared() > 1e-12f ? Vector3.Normalize(path) : Vector3.UnitZ) * approach;
            // The vehicle frames the view, not the fragments: one long spall line or ricochet would zoom far out
            var points = new List<Vector3>(Points.Length);
            foreach (Vector3 point in Points)
                if (Vectors.Finite(point))
                    points.Add(point);
            if (points.Count == 0)
                foreach (Line line in Lines)
                {
                    points.Add(line.From);
                    points.Add(line.To);
                }
            (Centre, Radius) = Framing.Fit(points, Entry);
        }
    }

    /// <summary>Tells the player's hits from others' and records them.</summary>
    internal static class Hits
    {
        internal const int Keep = 5, MaxLines = 512;
        // How long after firing a shell's hit still counts as the player's
        private const float Window = 15f;

        internal static readonly List<Hit> Recent = new();
        private static readonly List<(float Mass, float Diameter, int Projectile, float Time)> shots = new();

        internal static void Fired(float mass, float diameter, int projectile)
        {
            float now = Time.time;
            shots.RemoveAll(s => now - s.Time > Window);
            shots.Add((mass, diameter, projectile, now));
        }

        /// <summary>
        /// Whether a hit is one of <paramref name="shots"/>: the same shell by
        /// ID, as other guns fire the same type. Type and time are checked too,
        /// so an ID used again in a later battle doesn't count.
        /// </summary>
        internal static bool Mine(List<(float Mass, float Diameter, int Projectile, float Time)> shots, float mass, float diameter, int projectile, float now) =>
            shots.Exists(s => s.Projectile == projectile
                && s.Mass == mass
                && s.Diameter == diameter
                && now - s.Time <= Window);

        internal static void Simulated(PenetratorInfo penetrator, IPenetrationSimulation? simulation)
        {
            if (simulation == null || shots.Count == 0)
                return;
            using var _ = MadiLib.Profile.Measure("shellCam: record hit");
            float now = Time.time;
            // As the shell struck: a round's hit handler may have changed the penetrator since (HEAT into its jet)
            PenetratorInfo shell = Impacts.Struck;
            if (!Mine(shots, shell.Mass, shell.Diameter, Impacts.StruckProjectile, now))
                return;
            try
            {
                PenetrationSimulation? sim = simulation.TryCast<PenetrationSimulation>();
                if (sim == null)
                    return;
                Hit? hit = Record(sim);
                if (hit == null)
                    return;
                hit.Pending = sim.Pointer;
                Recent.Insert(0, hit);
                if (Recent.Count > Keep)
                    Recent.RemoveAt(Recent.Count - 1);
                if (Plugin.AutoReplay.Value)
                    Replay.Start(0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Couldn't record a hit: {e}");
            }
        }

        /// <summary>The game's about to apply <paramref name="simulation"/>'s damage: the health its parts have now is their health before.</summary>
        internal static void Applying(IntPtr simulation) => EachPart(simulation, part => part.Before = part.Health);

        /// <summary>The game's applied <paramref name="simulation"/>'s damage: the health its parts have now is what the hit left them.</summary>
        internal static void Applied(IntPtr simulation)
        {
            EachPart(simulation, part => part.After = part.Health);
            Hit? hit = Recent.Find(h => h.Pending == simulation);
            if (hit != null)
                hit.Pending = IntPtr.Zero;
        }

        private static void EachPart(IntPtr simulation, Action<Part> action)
        {
            Hit? hit = Recent.Find(h => h.Pending == simulation);
            if (hit == null)
                return;
            try
            {
                hit.Parts.ForEach(action);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Couldn't read part health for the replay: {e.Message}");
            }
        }

        private static Hit? Record(PenetrationSimulation sim)
        {
            int count = Math.Min(sim.FragmentCount, MaxLines);
            if (count == 0)
                return null;
            var hit = new Hit();
            var raw = new (Vector3 Spawn, Vector3 Direction, float Travel, float Speed, short Parent, FragmentFlag Flags)[count];
            var parents = new short[count];
            var durations = new float[count];
            for (int i = 0; i < count; i++)
            {
                raw[i] = Native.Fragment(sim.Pointer, i);
                // A fragment killed before it flew keeps the length it copied from its parent
                if ((raw[i].Flags & FragmentFlag.Killed) != 0)
                    raw[i].Travel = 0f;
                parents[i] = raw[i].Parent;
                durations[i] = raw[i].Travel / Math.Max(raw[i].Speed, 1f);
            }
            float[] starts = Timeline.Starts(parents, durations);
            bool penetrated = false, deflected = false;
            int dropped = 0;
            for (int i = 0; i < count; i++)
            {
                (Vector3 spawn, Vector3 direction, float travel, float speed, short _, FragmentFlag flags) = raw[i];
                bool shell = (flags & FragmentFlag.OriginalPenetrator) != 0;
                bool bounced = (flags & (FragmentFlag.Deflected | FragmentFlag.GougeDeflect)) != 0;
                if (shell)
                {
                    penetrated |= (flags & FragmentFlag.Penetrated) != 0;
                    deflected |= bounced;
                }
                Vector3 end = spawn + direction * travel;
                // A bad fragment (NaN or infinite) would poison the framing and every point projected with it
                bool finite = Vectors.Finite(spawn)
                    && Vectors.Finite(end)
                    && float.IsFinite(starts[i] + durations[i])
                    && float.IsFinite(speed);
                if (!finite)
                {
                    dropped++;
                    continue;
                }
                hit.Lines.Add(new Line(spawn, end, starts[i], durations[i], speed, shell, bounced));
                hit.End = Math.Max(hit.End, starts[i] + durations[i]);
                if (!shell)
                    hit.MaxSpeed = Math.Max(hit.MaxSpeed, speed);
            }
            if (hit.Lines.Count == 0)
                return null;
            try
            {
                int bad;
                int[] objects;
                (hit.Points, hit.Triangles, objects, bad) = ArmourMesh.Read(sim);
                if (bad > 0)
                    Plugin.Logger.LogWarning($"Hit's armour has {bad} non-finite vertices (a broken vehicle file?); left out of the replay");
                Health.Read(sim, objects, hit);
            }
            catch (Exception e)
            {
                // The lines alone still replay
                Plugin.Logger.LogWarning($"Couldn't read the armour hit, replaying without it: {e.Message}");
            }
            if (dropped > 0)
                Plugin.Logger.LogWarning($"Left {dropped} non-finite fragments out of a hit's replay");
            hit.Frame(Replay.Approach);
            int spall = hit.Lines.FindAll(l => !l.Shell).Count;
            string outcome = "stopped";
            if (penetrated)
                outcome = "penetrated";
            else if (deflected)
                outcome = "ricochet";
            if (spall > 0)
                outcome += $", {spall} fragments";
            hit.Outcome = outcome;
            return hit;
        }
    }

    internal static class Vectors
    {
        internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    /// <summary>
    /// Finds which of a hit's triangles belong to parts with health (crew,
    /// engine, ammo racks...): a triangle's object ID is its part's entity ID
    /// in the vehicle's health register, the simulation's damage applier.
    /// One mesh can hold many objects.
    /// </summary>
    internal static class Health
    {
        // Longer than any chain of pools on one object; only there so a bad link can't loop
        private const int MaxChain = 16;

        private static bool warned;

        internal static void Read(PenetrationSimulation sim, int[] triangleObjects, Hit hit)
        {
            try
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ObjectInfo>? objects = sim.objects;
                VehicleHealthRegister? register = sim.damageApplier?.TryCast<VehicleHealthRegister>();
                Il2CppSystem.Collections.Generic.Dictionary<int, HealthPool>? lookup = register?.register?.lookup;
                if (objects == null || lookup == null)
                    return;
                var heads = new Dictionary<IntPtr, HealthPool>();
                IntPtr HeadOf(int index)
                {
                    // Past the end, or a negative index
                    if ((uint)index >= objects.Length)
                        return IntPtr.Zero;
                    if (!lookup.TryGetValue(objects[index].ID, out HealthPool pool))
                        return IntPtr.Zero;
                    if (pool == null)
                        return IntPtr.Zero;
                    heads[pool.Pointer] = pool;
                    return pool.Pointer;
                }
                (hit.Owners, List<IntPtr> parts) = Assign(triangleObjects, HeadOf);
                Dictionary<IntPtr, string> names = Names(register!);
                foreach (IntPtr head in parts)
                {
                    var chain = new List<HealthPool>();
                    for (HealthPool? pool = heads[head]; pool != null && chain.Count < MaxChain; pool = pool.next)
                        chain.Add(pool);
                    string name = "Part";
                    HealthPool? named = chain.Find(p => names.ContainsKey(p.Pointer));
                    if (named != null)
                        name = names[named.Pointer];
                    hit.Parts.Add(new Part(chain.ToArray(), name));
                }
            }
            catch (Exception e)
            {
                // The armour still replays, just without health
                if (!warned)
                    Plugin.Logger.LogWarning($"Couldn't read part health for the replay: {e.Message}");
                warned = true;
            }
        }

        /// <summary>
        /// Groups triangles into parts: <paramref name="poolOf"/> gives an
        /// object's health pool, or zero for none; objects sharing a pool are
        /// one part. Returns each triangle's part (-1 for none) and each part's pool.
        /// </summary>
        internal static (int[] Owners, List<IntPtr> Parts) Assign(int[] triangleObjects, Func<int, IntPtr> poolOf)
        {
            var owners = new int[triangleObjects.Length];
            var parts = new List<IntPtr>();
            var partOfObject = new Dictionary<int, int>();
            var partOfPool = new Dictionary<IntPtr, int>();
            for (int t = 0; t < triangleObjects.Length; t++)
            {
                int index = triangleObjects[t];
                if (!partOfObject.TryGetValue(index, out int part))
                {
                    part = -1;
                    IntPtr pool = poolOf(index);
                    if (pool != IntPtr.Zero && !partOfPool.TryGetValue(pool, out part))
                    {
                        part = parts.Count;
                        parts.Add(pool);
                        partOfPool[pool] = part;
                    }
                    partOfObject[index] = part;
                }
                owners[t] = part;
            }
            return (owners, parts);
        }

        /// <summary>Each health pool's part, named by its type, such as "Ammo rack".</summary>
        private static Dictionary<IntPtr, string> Names(VehicleHealthRegister register)
        {
            var names = new Dictionary<IntPtr, string>();
            Il2CppSystem.Collections.Generic.List<Sprocket.Vehicles.VehicleComponentHealth>? entries = register.entries;
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                Sprocket.Vehicles.VehicleComponentHealth? entry = entries[i];
                if (entry?.HealthPool != null && entry.Owner != null)
                    names[entry.HealthPool.Pointer] = Label(entry.Owner.GetIl2CppType().Name);
            }
            return names;
        }

        /// <summary>A type name as words: "AmmoRack" to "Ammo rack".</summary>
        internal static string Label(string type)
        {
            var words = new System.Text.StringBuilder();
            foreach (char c in type)
            {
                if (char.IsUpper(c) && words.Length > 0)
                    words.Append(' ').Append(char.ToLowerInvariant(c));
                else
                    words.Append(c);
            }
            return words.ToString();
        }
    }

    /// <summary>What the replay frames.</summary>
    internal static class Framing
    {
        // Share of points left out at each end of each axis, so a few stray ones don't count; largest radius framed, metres
        private const float Trim = 0.02f, MaxRadius = 10f, MinRadius = 0.5f;

        /// <summary>
        /// A sphere round most of <paramref name="points"/> (the outermost
        /// <see cref="Trim"/> on each axis left out) that also holds
        /// <paramref name="must"/>, its radius kept within sensible limits.
        /// </summary>
        internal static (Vector3 Centre, float Radius) Fit(List<Vector3> points, Vector3 must)
        {
            if (points.Count == 0)
                return (must, MinRadius);
            float Low(float[] v) => v[(int)(Trim * (v.Length - 1))];
            float High(float[] v) => v[(int)MathF.Ceiling((1f - Trim) * (v.Length - 1))];
            float[] Sorted(Func<Vector3, float> axis)
            {
                float[] v = points.ConvertAll(p => axis(p)).ToArray();
                Array.Sort(v);
                return v;
            }
            float[] x = Sorted(p => p.X), y = Sorted(p => p.Y), z = Sorted(p => p.Z);
            Vector3 min = Vector3.Min(new Vector3(Low(x), Low(y), Low(z)), must);
            Vector3 max = Vector3.Max(new Vector3(High(x), High(y), High(z)), must);
            Vector3 centre = (min + max) / 2f;
            float radius = Math.Clamp((max - min).Length() / 2f, MinRadius, MaxRadius);
            // Capped, the sphere is moved to keep the entry in view
            if ((must - centre).Length() > radius)
                centre = must + Vector3.Normalize(centre - must) * radius * 0.5f;
            return (centre, radius);
        }
    }

    /// <summary>When each fragment starts: at its parent's end, the first ones at 0.</summary>
    internal static class Timeline
    {
        /// <param name="parents">Each fragment's parent index; negative, or not before it, for none.</param>
        /// <param name="durations">Each fragment's flight time.</param>
        internal static float[] Starts(short[] parents, float[] durations)
        {
            var starts = new float[parents.Length];
            for (int i = 0; i < parents.Length; i++)
            {
                int parent = parents[i];
                // Parents come before their children; anything else is treated as a root, so a bad index can't loop
                starts[i] = parent >= 0 && parent < i ? starts[parent] + durations[parent] : 0f;
            }
            return starts;
        }
    }

    /// <summary>
    /// Reads a simulation's fragments at their native offsets (its armour
    /// is <see cref="ArmourMesh.Read"/>): <c>Fragment</c> is a struct that
    /// Il2CppInterop wraps as classes, so its arrays of them read garbage
    /// (see highExplosive's <c>Fragments</c>).
    /// </summary>
    internal static class Native
    {

        private static bool ready;
        private static int fragments, fragmentSize, spawn, direction, travel, speed, parent, flags;

        private static void Init()
        {
            fragments = MadiLib.Native.FieldOffset<PenetrationSimulation>("fragments");
            fragmentSize = MadiLib.Native.ValueSize<Fragment>();
            spawn = MadiLib.Native.StructOffset<Fragment>("spawnPoint");
            direction = MadiLib.Native.StructOffset<Fragment>("direction");
            travel = MadiLib.Native.StructOffset<Fragment>("travelDistance");
            speed = MadiLib.Native.StructOffset<Fragment>("speed");
            parent = MadiLib.Native.StructOffset<Fragment>("parentIndex");
            flags = MadiLib.Native.StructOffset<Fragment>("flags");
            ready = true;
        }

        private static float Float(IntPtr at, int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at, offset));
        private static Vector3 Vector(IntPtr at, int offset) => new(Float(at, offset), Float(at, offset + 4), Float(at, offset + 8));

        internal static (Vector3 Spawn, Vector3 Direction, float Travel, float Speed, short Parent, FragmentFlag Flags) Fragment(IntPtr simulation, int index)
        {
            if (!ready)
                Init();
            IntPtr at = MadiLib.Native.Element(Marshal.ReadIntPtr(simulation, fragments), index, fragmentSize);
            return (Vector(at, spawn), Vector(at, direction), Float(at, travel), Float(at, speed), Marshal.ReadInt16(at, parent), (FragmentFlag)(ushort)Marshal.ReadInt16(at, flags));
        }
    }
}
