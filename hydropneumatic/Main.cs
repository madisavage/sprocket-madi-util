extern alias UnityCoreModule;
extern alias UnityPhysicsModule;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MadiLib;
using Properties;
using Sprocket.Blueprints;
using Sprocket.ContinuousTracks;
using Sprocket.UI;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Tracks.Editor;
using Quaternion = UnityCoreModule::UnityEngine.Quaternion;
using Time = UnityCoreModule::UnityEngine.Time;
using Transform = UnityCoreModule::UnityEngine.Transform;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;
using ForceMode = UnityPhysicsModule::UnityEngine.ForceMode;
using Physics = UnityPhysicsModule::UnityEngine.Physics;
using Rigidbody = UnityPhysicsModule::UnityEngine.Rigidbody;

namespace Hydropneumatic
{
    /// <summary>
    /// BepInEx entry point. Hydropneumatic suspension: on vehicles whose
    /// tracks are tagged <c>[adjust]</c>, keybinds change the player's ride
    /// height, pitch and roll while driving.
    /// </summary>
    /// <remarks>
    /// The tag is in the track blueprint's name, saved in vehicle files but
    /// not shown in the game, so the setting travels with shared vehicles.
    /// </remarks>
    // HarmonyPatch on the class: PatchAll only looks at patch methods in classes that have it
    [HarmonyPatch]
    [BepInPlugin("madi.hydropneumatic", "hydropneumatic", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal const float DefaultTravel = 150f, DefaultRate = 50f;

        // [adjust], [adjust 150] for ±150 mm travel, [adjust 150 50] for 50 mm/s too
        private static readonly Regex tag = new(@"\s*\[adjust(?:\s+([0-9]*\.?[0-9]+))?(?:\s+([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);
        // Any [adjust ...], to tell a bad tag from none
        private static readonly Regex anyTag = new(@"\[adjust\b[^\]]*\]", RegexOptions.IgnoreCase);

        internal static BepInEx.Logging.ManualLogSource Logger = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;
        internal static Func<bool> Raise = null!, Lower = null!, PitchUp = null!, PitchDown = null!, RollLeft = null!, RollRight = null!;

        /// <summary>
        /// Pitch (degrees, nose up) the player's suspension moves toward at its
        /// rate, for hull aiming; null for manual. A pitch key sets it back to null.
        /// </summary>
        public static float? PitchTarget { get; set; }

        public override void Load()
        {
            Logger = Log;
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show your vehicle's ride height, pitch and roll while it isn't level.");
            SettingsTab.AddSection("Hydropneumatic", "Hydropneumatic", ShowIndicator);
            Hud.AddPanel(Indicator);
            Raise = Keybinds.RegisterHeld(this, "Hydropneumatic", "Raise", "PageUp");
            Lower = Keybinds.RegisterHeld(this, "Hydropneumatic", "Lower", "PageDown");
            PitchUp = Keybinds.RegisterHeld(this, "Hydropneumatic", "Pitch up", "Home");
            PitchDown = Keybinds.RegisterHeld(this, "Hydropneumatic", "Pitch down", "End");
            RollLeft = Keybinds.RegisterHeld(this, "Hydropneumatic", "Roll left", "Insert");
            RollRight = Keybinds.RegisterHeld(this, "Hydropneumatic", "Roll right", "Delete");
            Keybinds.Register(this, "Hydropneumatic", "Level", "Backspace", Ride.Level);
            Keybinds.Register(this, "Hydropneumatic", "Toggle hull aiming", "Alt+H", HullAim.Toggle);
            new Harmony("madi.hydropneumatic").PatchAll();
        }

        /// <summary>The HUD line for the player's vehicle while it isn't level.</summary>
        private static string? Indicator()
        {
            using var _ = MadiLib.Profile.Measure("hydropneumatic: HUD indicator");
            Ride.Prune();
            Pose.State pose = Ride.Current;
            if (!ShowIndicator.Value || !Ride.Active || pose == default && !HullAim.Active)
                return null;
            string line = $"Suspension: {pose.Height:+0;-0;0} mm, pitch {pose.Pitch:+0.0;-0.0;0.0}°, roll {pose.Roll:+0.0;-0.0;0.0}°";
            return HullAim.Active ? line + (HullAim.Enabled ? ", hull aim on" : ", hull aim off") : line;
        }

        /// <summary>
        /// The travel (± mm) and rate (mm/s) in <paramref name="name"/>'s
        /// <c>[adjust]</c> tag, defaults for missing numbers; null when
        /// untagged or the tag doesn't parse, so the vehicle stays stock.
        /// </summary>
        internal static (float Travel, float Rate)? Settings(string? name) => Numbers(tag, anyTag, name, DefaultTravel, DefaultRate);

        /// <summary>
        /// The two numbers in <paramref name="name"/>'s <paramref name="tag"/>, defaults for missing ones;
        /// null when untagged or when <paramref name="anyTag"/> finds one that doesn't parse.
        /// </summary>
        internal static (float, float)? Numbers(Regex tag, Regex anyTag, string? name, float first, float second)
        {
            Match match = tag.Match(name ?? "");
            if (!match.Success || anyTag.Match(name!).Value != match.Value.Trim())
                return null;
            float Number(Group group, float fallback) => group.Success ? float.Parse(group.Value, CultureInfo.InvariantCulture) : fallback;
            return (Number(match.Groups[1], first), Number(match.Groups[2], second));
        }

        // Track blueprint, its travel and rate properties
        private static readonly ObjectCache<FloatProperty> travels = new(), rates = new();

        /// <summary>
        /// Draws an "Adjustable suspension" checkbox in the track panel and, when
        /// ticked, its travel and rate; all edit the tag in the track blueprint's name.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(TrackEditor), nameof(TrackEditor.OnGUI))]
        private static void Draw(TrackEditor __instance, IGUILayout __0)
        {
            var slot = __instance.Component?.blueprintSlot;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null || slot?.Blueprint is not { } blueprint)
                return;
            // The panel's working copy may be saved back over the slot's: tag both
            TrackBlueprint? copy = __instance.blueprint;
            void Set((float, float)? settings)
            {
                blueprint.Name = WithSettings(blueprint.Name ?? "", settings);
                if (copy != null && copy.Pointer != blueprint.Pointer)
                    copy.Name = blueprint.Name;
                slot.MarkModified();
            }
            var settings = Settings(blueprint.Name);
            drawer.ToggleField(
                "Adjustable suspension",
                settings != null,
                (Action<bool>)(on =>
                {
                    Set(on ? (DefaultTravel, DefaultRate) : null);
                    __instance.RequestRedraw();
                }),
                "Hydropneumatic: change ride height, pitch and roll in battle with the Hydropneumatic keybinds. Only your own vehicle adjusts. Saved with the vehicle.");
            if (settings == null)
                return;
            drawer.Float(travels.Get(blueprint, () => new FloatProperty(
                "Suspension travel",
                (Func<float>)(() => Settings(blueprint.Name)?.Travel ?? DefaultTravel),
                (Action<float>)(value => Set((value, Settings(blueprint.Name)?.Rate ?? DefaultRate))),
                "How far (mm) each wheel may move up or down from level. The suspension's own travel also limits height.")
            {
                Min = 10f,
                Max = 1000f,
                SoftMin = 10f,
                SoftMax = 300f,
                UseSoftLimits = true,
                Step = 5f
            }));
            drawer.Float(rates.Get(blueprint, () => new FloatProperty(
                "Suspension rate",
                (Func<float>)(() => Settings(blueprint.Name)?.Rate ?? DefaultRate),
                (Action<float>)(value => Set((Settings(blueprint.Name)?.Travel ?? DefaultTravel, value))),
                "How fast (mm/s) the outermost wheel moves while adjusting.")
            {
                Min = 1f,
                Max = 500f,
                SoftMin = 1f,
                SoftMax = 150f,
                UseSoftLimits = true,
                Step = 1f
            }));
        }

        /// <summary><paramref name="name"/> with its tag replaced by one for <paramref name="settings"/>, or removed for null.</summary>
        internal static string WithSettings(string name, (float Travel, float Rate)? settings) => WithNumbers(tag, "adjust", name, settings);

        /// <summary><paramref name="name"/> with its <paramref name="tag"/> replaced by <c>[word first second]</c>, or removed for null.</summary>
        internal static string WithNumbers(Regex tag, string word, string name, (float, float)? numbers)
        {
            name = tag.Replace(name, "").Trim();
            if (numbers is var (first, second))
                name = $"{name} [{word} {first.ToString("0.#", CultureInfo.InvariantCulture)} {second.ToString("0.#", CultureInfo.InvariantCulture)}]".Trim();
            return name;
        }
    }

    /// <summary>
    /// Moves tagged tracks on the player's vehicle: height and roll through
    /// each track's suspension preload, pitch through strut forces.
    /// </summary>
    /// <remarks>
    /// The game holds the hull up per track, from the preload in the track's
    /// physics job (metres of ride height), not per arm: no per-arm spring
    /// field moves the hull. So pitch is pushed in by equal and opposite forces
    /// at the loaded arms' mounts, as struts would, never lifting the hull.
    /// Fields are read through raw pointers, as <c>Stabilizer.Axes</c> does.
    /// </remarks>
    [HarmonyPatch]
    internal static class Ride
    {
        private sealed class Track
        {
            public readonly TrackBehaviour Behaviour;
            public readonly (float Travel, float Rate)? Settings;
            public float Stock = float.NaN;
            public bool Written, Failed;
            // This step's loaded arms: world position of the mount
            public readonly List<Vector3> Loaded = new();
            // This step's wheels: position along the hull (m) and compression (m)
            public readonly List<(float X, float Compression)> Wheels = new();
            public int Arms;
            // Span of the arms along the hull, in metres
            public float MinX, MaxX;
            // Half the arms' travel at the wheel, in mm: how far the suspension itself can move each way
            public float Reach = float.MaxValue;

            public Track(TrackBehaviour behaviour, (float, float)? settings) => (Behaviour, Settings) = (behaviour, settings);
        }

        // Pitch controller: angular acceleration (rad/s²) = Kp·error + Ki·integral - Kd·rate
        private const float Kp = 40f, Ki = 5f, Kd = 12f, MaxIntegral = 0.5f;
        // Seconds over which level pitch follows the hull while not pitched
        private const float LevelSmoothing = 2f;

        internal static readonly bool Ready;
        private static readonly int springsField, statesField, preloadField, armLengthField, springSize, originOffset, minOffset, maxOffset, stateSize, angleOffset;

        // TrackBehaviour, its state; untagged tracks too, so the tag is read once
        private static readonly Dictionary<IntPtr, Track> tracks = new();
        private static readonly List<IntPtr> dead = new();

        /// <summary>The player's pose; level on a new vehicle.</summary>
        internal static Pose.State Current;
        private static Pose.State? target;
        private static Transform? root;
        private static float seen, levelPitch = float.NaN, integral, halfLength = 2f, halfWidth = 1f;
        private static bool capped;
        private static float smoothedMoment;
        // Seconds over which the strut moment follows the controller, so arms loading and unloading don't slam it
        private const float MomentSmoothing = 0.1f;
        private static readonly List<(float X, float Compression)> wheels = new();

        /// <summary>Whether the player's vehicle has tagged tracks, as of the last second.</summary>
        internal static bool Active => Time.realtimeSinceStartup - seen < 1f;

        static Ride()
        {
            try
            {
                int Field(IntPtr type, string name) => (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(type, name));
                // Struct field offsets count the object header, which a struct inside its owner doesn't have
                int StructField(IntPtr type, string name) => Field(type, name) - 2 * IntPtr.Size;
                IntPtr track = Il2CppClassPointerStore<TrackBehaviour>.NativeClassPtr;
                springsField = Field(track, "springs");
                statesField = Field(track, "springStates");
                IntPtr job = Il2CppClassPointerStore<TrackUpdateInfo>.NativeClassPtr;
                preloadField = Field(track, "physicsJob") + StructField(job, "suspensionPreload");
                armLengthField = Field(track, "physicsJob") + StructField(job, "suspensionArmLength");
                uint align = 0;
                IntPtr spring = Il2CppClassPointerStore<Sprocket.ContinuousTracks.SuspensionSpring>.NativeClassPtr;
                springSize = IL2CPP.il2cpp_class_value_size(spring, ref align);
                originOffset = StructField(spring, "origin");
                minOffset = StructField(spring, "minAngle");
                maxOffset = StructField(spring, "maxAngle");
                IntPtr state = Il2CppClassPointerStore<SuspensionState>.NativeClassPtr;
                stateSize = IL2CPP.il2cpp_class_value_size(state, ref align);
                angleOffset = StructField(state, "angle");
                Ready = springSize > 0 && stateSize > 0;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Couldn't find the track physics fields; suspension won't adjust: {e.Message}");
            }
            if (!Ready)
                Plugin.Logger.LogError("Track physics fields not found; suspension won't adjust.");
        }

        /// <summary>Starts the player's suspension back to level, at its rate.</summary>
        internal static void Level()
        {
            target = default(Pose.State);
            Plugin.PitchTarget = null;
        }

        /// <summary>A NativeArray at <paramref name="p"/>: buffer pointer first, length after it.</summary>
        private static unsafe (IntPtr Buffer, int Length) Array(TrackBehaviour track, int field)
        {
            byte* p = (byte*)track.Pointer + field;
            return (*(IntPtr*)p, *(int*)(p + IntPtr.Size));
        }

        /// <summary>
        /// Whether <paramref name="track"/> was released with its vehicle: its
        /// wrapper lives on, but its native buffers are freed.
        /// </summary>
        private static bool Released(TrackBehaviour track) => track.Transform == null || Array(track, springsField).Buffer == IntPtr.Zero;

        /// <summary>
        /// Whether <paramref name="track"/> is the player's this step. A destroyed
        /// vehicle's tracks stop stepping with their last state, so check release too.
        /// </summary>
        private static bool Live(Track track) => track.Written && !Released(track.Behaviour);

        /// <summary>Drops released tracks, so the list doesn't grow each battle.</summary>
        internal static void Prune()
        {
            dead.Clear();
            foreach (var (pointer, track) in tracks)
                if (Released(track.Behaviour))
                    dead.Add(pointer);
            foreach (IntPtr pointer in dead)
                tracks.Remove(pointer);
        }

        private static Track Get(TrackBehaviour behaviour)
        {
            if (!tracks.TryGetValue(behaviour.Pointer, out Track? track))
                tracks[behaviour.Pointer] = track = new Track(behaviour,
                    Plugin.Settings(behaviour.Transform?.GetComponent<TrackAssembly>()?.blueprintSlot?.Blueprint?.Name));
            return track;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(TrackBehaviour), nameof(TrackBehaviour.FixedUpdate))]
        private static unsafe void Step(TrackBehaviour __instance, float __0)
        {
            using var _ = MadiLib.Profile.Measure("hydropneumatic: physics step");
            if (!Ready || Released(__instance))
                return;
            Track track = Get(__instance);
            if (track.Settings == null || track.Failed)
                return;
            float* preload = (float*)((byte*)__instance.Pointer + preloadField);
            try
            {
                if (float.IsNaN(track.Stock))
                    track.Stock = *preload;
                if (!PlayerVehicle.Contains(__instance.Transform) || __instance.broken)
                {
                    // No longer the player's (or thrown): back to stock once
                    if (track.Written)
                        *preload = track.Stock;
                    track.Written = false;
                    track.Loaded.Clear();
                    return;
                }
                seen = Time.realtimeSinceStartup;
                *preload = track.Stock + Pose.Preload(Current, __instance.sideMultiplier, halfWidth);
                track.Written = true;
                Arms(track);
                // Once per step, after the right track: both tracks' arms are in
                if (__instance.sideMultiplier > 0)
                    Struts(__0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Couldn't adjust a track's suspension; leaving it stock: {e}");
                *preload = track.Stock;
                track.Failed = true;
            }
        }

        /// <summary>Records <paramref name="track"/>'s loaded arms (not fully extended) and its arms' span.</summary>
        private static unsafe void Arms(Track track)
        {
            TrackBehaviour behaviour = track.Behaviour;
            var (springs, length) = Array(behaviour, springsField);
            var (states, count) = Array(behaviour, statesField);
            var arms = behaviour.suspensionArmTransforms;
            track.Loaded.Clear();
            track.Wheels.Clear();
            track.Arms = 0;
            if (arms == null)
                return;
            float armLength = *(float*)((byte*)behaviour.Pointer + armLengthField);
            track.MinX = float.MaxValue;
            track.MaxX = float.MinValue;
            track.Reach = float.MaxValue;
            for (int i = 0; i < length && i < count && i < arms.Length; i++)
            {
                byte* spring = (byte*)springs + i * springSize;
                float x = *(float*)(spring + originOffset);
                track.MinX = MathF.Min(track.MinX, x);
                track.MaxX = MathF.Max(track.MaxX, x);
                track.Arms++;
                float angle = *(float*)((byte*)states + i * stateSize + angleOffset), max = *(float*)(spring + maxOffset);
                track.Reach = MathF.Min(track.Reach, armLength * (max - *(float*)(spring + minOffset)) * 1000f / 2f);
                // Compression from full extension: the game's travel is the arm length times the angle range
                track.Wheels.Add((x, armLength * (max - angle)));
                // An arm at full extension carries no load, so a strut can't push or pull through it
                if (angle < max - 0.01f)
                    track.Loaded.Add(arms[i].position);
            }
        }

        /// <summary>
        /// Pitches the player's hull toward the pose's pitch with strut forces
        /// at the loaded arms; at 0 it leaves the hull to its springs and
        /// follows its pitch as level.
        /// </summary>
        /// <remarks>
        /// Pitch is measured against the wheels, not the world, so terrain,
        /// braking and acceleration aren't errors to fight.
        /// </remarks>
        private static void Struts(float deltaTime)
        {
            if (PlayerVehicle.Root is not { } hull || hull.GetComponent<Rigidbody>() is not { } body)
                return;
            wheels.Clear();
            foreach (Track track in tracks.Values)
                if (Live(track))
                    wheels.AddRange(track.Wheels);
            float pitch = Pose.TrackPitch(wheels);
            if (Current.Pitch == 0f)
            {
                // Smoothed, so the level pitching starts from isn't one bump's
                levelPitch = float.IsNaN(levelPitch) ? pitch : levelPitch + (pitch - levelPitch) * MathF.Min(1f, deltaTime / LevelSmoothing);
                integral = 0f;
                smoothedMoment = 0f;
                return;
            }
            Vector3 right = hull.right, up = hull.up, forward = hull.forward;
            float error = (levelPitch + Current.Pitch - pitch) * MathF.PI / 180f;
            // Nose-up rate: turning about +right tips the nose down
            float rate = -Vector3.Dot(body.angularVelocity, right);
            // Not while capped: the error can't close, and a wound-up integral overshoots once it can
            if (!capped)
                integral = Math.Clamp(integral + error * deltaTime, -MaxIntegral, MaxIntegral);
            float accel = Kp * error + Ki * integral - Kd * rate;
            float moment = accel * Inertia(body, right);
            moment = smoothedMoment += (moment - smoothedMoment) * MathF.Min(1f, deltaTime / MomentSmoothing);

            Vector3 centre = body.worldCenterOfMass;
            int front = 0, rear = 0, arms = 0;
            float frontLever = 0f, rearLever = 0f;
            foreach (Track track in tracks.Values)
            {
                if (!Live(track))
                    continue;
                arms += track.Arms;
                foreach (Vector3 position in track.Loaded)
                {
                    float x = Vector3.Dot(position - centre, forward);
                    if (x > 0f) { front++; frontLever += x; }
                    else { rear++; rearLever -= x; }
                }
            }
            if (front == 0 || rear == 0)
                return;
            (float frontForce, float rearForce, capped) = Pose.Struts(moment, frontLever / front, rearLever / rear, front, rear, body.mass * Physics.gravity.magnitude, arms);
            foreach (Track track in tracks.Values)
                if (Live(track))
                    foreach (Vector3 position in track.Loaded)
                        body.AddForceAtPosition(up * (Vector3.Dot(position - centre, forward) > 0f ? frontForce : rearForce), position, ForceMode.Force);
        }

        /// <summary>The moment of inertia of <paramref name="body"/> about world <paramref name="axis"/>, from its principal moments.</summary>
        internal static float Inertia(Rigidbody body, Vector3 axis)
        {
            Vector3 local = Quaternion.Inverse(body.rotation * body.inertiaTensorRotation) * axis;
            Vector3 tensor = body.inertiaTensor;
            return tensor.x * local.x * local.x + tensor.y * local.y * local.y + tensor.z * local.z * local.z;
        }

        /// <summary>Moves the player's pose by the held keys or toward its target, once a frame.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(TrackBehaviour), nameof(TrackBehaviour.Update))]
        private static void Frame(TrackBehaviour __instance)
        {
            // Once a frame: the left track's call, on a tagged player track
            if (__instance.sideMultiplier > 0 || !Ready || Released(__instance) || Get(__instance) is not { Settings: var (tagTravel, rate) }
                || PlayerVehicle.Root is not { } hull || !PlayerVehicle.Contains(__instance.Transform))
                return;
            using var _ = MadiLib.Profile.Measure("hydropneumatic: frame");
            if (hull != root)
            {
                root = hull;
                levelPitch = float.NaN;
                Prune();
                Current = default;
                target = null;
                Plugin.PitchTarget = null;
            }
            // Never past what the suspension itself can reach: an unreachable pose leaves the struts slamming against its stops
            float travel = MathF.Min(tagTravel, Measure(hull));

            float dt = Time.deltaTime, step = rate * dt;
            float pitchStep = MathF.Atan(step / (1000f * halfLength)) * 180f / MathF.PI;
            float rollStep = MathF.Atan(step / (1000f * halfWidth)) * 180f / MathF.PI;
            float Axis(Func<bool> plus, Func<bool> minus) => (plus() ? 1f : 0f) - (minus() ? 1f : 0f);
            float dHeight = Axis(Plugin.Raise, Plugin.Lower) * step;
            float dPitch = Axis(Plugin.PitchUp, Plugin.PitchDown) * pitchStep;
            // Roll left: lean left, the right side up
            float dRoll = Axis(Plugin.RollLeft, Plugin.RollRight) * rollStep;
            if (dHeight != 0f || dPitch != 0f || dRoll != 0f)
            {
                target = null;
                if (dPitch != 0f)
                    Plugin.PitchTarget = null;
                Current = Pose.Move(Current, dHeight, dPitch, dRoll, travel, halfLength, halfWidth);
            }
            else if (target is { } level)
            {
                Current = Pose.Toward(Current, level, dt, rate, halfLength, halfWidth, travel);
                if (Current == level)
                    target = null;
            }
            else if (Plugin.PitchTarget is { } pitch)
                Current = Pose.Toward(Current, Current with { Pitch = pitch }, dt, rate, halfLength, halfWidth, travel);
        }

        /// <summary>Sets half the wheelbase and half the track spacing of the player's tagged tracks.</summary>
        /// <returns>How far (mm) the suspension itself can move a wheel each way.</returns>
        private static float Measure(Transform hull)
        {
            float minX = float.MaxValue, maxX = float.MinValue, left = float.NaN, right = float.NaN, reach = float.MaxValue;
            foreach (Track track in tracks.Values)
            {
                if (!Live(track) || track.Arms == 0 || track.Behaviour.Transform is not { } transform)
                    continue;
                minX = MathF.Min(minX, track.MinX);
                maxX = MathF.Max(maxX, track.MaxX);
                reach = MathF.Min(reach, track.Reach);
                float side = Vector3.Dot(transform.position - hull.position, hull.right);
                if (track.Behaviour.sideMultiplier < 0) left = side; else right = side;
            }
            if (maxX > minX)
                halfLength = (maxX - minX) / 2f;
            if (!float.IsNaN(left) && !float.IsNaN(right) && right > left)
                halfWidth = (right - left) / 2f;
            return reach;
        }
    }
}
