extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MadiLib;
using Properties;
using Sprocket;
using Sprocket.Blueprints;
using Sprocket.UI;
using Sprocket.Vehicles.Turrets;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.Vehicles.Weapons;
using VehicleDesigner.Compartments.Design;
using Mathf = UnityCoreModule::UnityEngine.Mathf;
using Quaternion = UnityCoreModule::UnityEngine.Quaternion;
using Time = UnityCoreModule::UnityEngine.Time;
using Transform = UnityCoreModule::UnityEngine.Transform;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace Stabilizer
{
    /// <summary>
    /// BepInEx entry point. Naive gun stabilization: traverse motors and laying
    /// drives marked as stabilized turn against the hull's rotation, so the
    /// gun holds its world direction while the drive keeps aiming as usual.
    /// </summary>
    /// <remarks>
    /// A drive is stabilized when its blueprint's name has the <c>[stab]</c>
    /// tag. Drive names are saved in vehicle files but not shown in the game,
    /// so the setting travels with shared vehicles.
    /// </remarks>
    [BepInPlugin("madi.stabilizer", "stabilizer", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        // [stab] or [stab 30] for a 30 km/h speed limit
        private static readonly Regex tag = new(@"\s*\[stab(?:\s+([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);

        // Drive blueprint, its speed limit property
        private static readonly ObjectCache<FloatProperty> limits = new();

        internal static BepInEx.Logging.ManualLogSource Logger = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;

        /// <summary>Whether the player's stabilizers are on; the keybind flips it. Other vehicles' always are.</summary>
        internal static bool PlayerEnabled = true;

        public override void Load()
        {
            Logger = Log;
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show whether your vehicle's stabilizers are on.");
            SettingsTab.AddSection("Stabilizer", "Stabilizer", ShowIndicator);
            Hud.AddPanel(Indicator);
            Keybinds.Register(this, "Stabilizer", "Toggle stabilizers", "Alt+S", () => PlayerEnabled = !PlayerEnabled);
            new Harmony("madi.stabilizer").PatchAll();
        }

        /// <summary>
        /// The HUD line for the player's vehicle: each stabilized drive, and
        /// whether it's on or off for being over its speed limit.
        /// </summary>
        private static string? Indicator()
        {
            using var _ = MadiLib.Profile.Measure("stabilizer: HUD indicator");
            float now = Time.realtimeSinceStartup;
            Drives.Prune(now);
            if (!ShowIndicator.Value)
                return null;
            var parts = new List<string>();
            foreach (var (part, name, speed, limit, seen) in Drives.Status.Values)
                if (now - seen < 1f && part != null && PlayerVehicle.Contains(part))
                    parts.Add(limit > 0f && speed > limit ? $"{name} off (over {limit:0} km/h)" : $"{name} on");
            if (parts.Count == 0)
                return null;
            return PlayerEnabled ? "Stabilizer: " + string.Join(", ", parts) : "Stabilizer: off";
        }

        /// <summary>Whether <paramref name="part"/>'s drive should stabilize: always, unless the player switched theirs off.</summary>
        internal static bool Enabled(Transform part) => PlayerEnabled || !PlayerVehicle.Contains(part);

        /// <summary>
        /// The speed limit (km/h, 0 for none) in <paramref name="blueprint"/>'s
        /// <c>[stab]</c> tag, or null when the drive isn't stabilized.
        /// </summary>
        internal static float? Limit(Blueprint? blueprint) => Limit(blueprint?.Name);

        /// <summary>The speed limit in <paramref name="name"/>'s <c>[stab]</c> tag, as <see cref="Limit(Blueprint?)"/>.</summary>
        internal static float? Limit(string? name)
        {
            Match match = tag.Match(name ?? "");
            if (!match.Success)
                return null;
            return match.Groups[1].Success ? float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0f;
        }

        /// <summary>Sets <paramref name="blueprint"/>'s tag for <paramref name="limit"/>, or removes it for null.</summary>
        private static void SetLimit(Blueprint blueprint, float? limit) => blueprint.Name = WithLimit(blueprint.Name ?? "", limit);

        /// <summary><paramref name="name"/> with its tag replaced by one for <paramref name="limit"/>, or removed for null.</summary>
        internal static string WithLimit(string name, float? limit)
        {
            name = tag.Replace(name, "").Trim();
            if (limit is { } value)
                name = (name + (value > 0f ? $" [stab {value.ToString("0.#", CultureInfo.InvariantCulture)}]" : " [stab]")).Trim();
            return name;
        }

        /// <summary>
        /// Draws a "Stabilizer" checkbox for <paramref name="blueprint"/> and,
        /// when ticked, its speed limit; both edit the tag in its name.
        /// </summary>
        internal static void Draw(IGUILayout? layout, Blueprint? blueprint, Action markModified, Action redraw)
        {
            IGUIElementDrawer? drawer = layout?.TryCast<IGUIElementDrawer>();
            if (drawer == null || blueprint == null)
                return;
            float? limit = Limit(blueprint);
            drawer.ToggleField(
                "Stabilizer",
                limit != null,
                (Action<bool>)(on =>
                {
                    SetLimit(blueprint, on ? 0f : null);
                    markModified();
                    redraw();
                }),
                "Turns the drive against the hull's rotation so the gun holds its direction. Saved with the vehicle.");
            if (limit == null)
                return;
            FloatProperty speedLimit = limits.Get(blueprint, () => new FloatProperty(
                "Stabilizer speed limit",
                (Func<float>)(() => Limit(blueprint) ?? 0f),
                (Action<float>)(value =>
                {
                    SetLimit(blueprint, value);
                    markModified();
                }),
                "Above this speed (km/h) the stabilizer switches off; 0 keeps it on at any speed.")
            {
                Min = 0f,
                Max = 1000f,
                SoftMin = 0f,
                SoftMax = 100f,
                UseSoftLimits = true,
                Step = 1f
            });
            drawer.Float(speedLimit);
        }
    }

    /// <summary>
    /// Stabilizes rotation axes by writing their angle in the game's
    /// <see cref="RotationAxisPhysicsRegister"/>, before the drive steps.
    /// </summary>
    /// <remarks>
    /// The register keeps every axis in one native array of structs, which
    /// Il2CppInterop wraps as an array of references, so it is read through
    /// raw pointers here. Angles are in degrees (the drives' limits are).
    /// Which way an angle turns its part isn't exposed, so each axis starts
    /// from a guess and corrects it by watching its angle and part move.
    /// </remarks>
    internal static class Axes
    {
        private sealed class State
        {
            public Quaternion Hull;
            public float Angle;
            public Vector3 Local;
            /// <summary>Degrees the part turns, around the world axis, per degree of axis angle: 1 or -1.</summary>
            public float Gain;
            public float Moved, Turned;
            public float Time;
            /// <summary>Hull speed in km/h.</summary>
            public float Speed;
            // Time and hull position since the speed was last measured
            public float Window;
            public Vector3 WindowPosition;
        }

        // Axis handle, its last step
        private static readonly Dictionary<int, State> states = new();

        private static readonly IntPtr axesField;
        private static readonly int size, angle, min, max;

        static Axes()
        {
            axesField = (IntPtr)AccessTools.Field(typeof(RotationAxisPhysicsRegister), "NativeFieldInfoPtr_axes").GetValue(null)!;
            IntPtr axis = Il2CppClassPointerStore<RotationAxisPhysics>.NativeClassPtr;
            uint align = 0;
            size = IL2CPP.il2cpp_class_value_size(axis, ref align);
            // Field offsets count the object header, which an unboxed struct in an array doesn't have
            int Offset(string name) => (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(axis, name)) - 2 * IntPtr.Size;
            angle = Offset("angle");
            min = Offset("min");
            max = Offset("max");
        }

        /// <summary>The axis <paramref name="handle"/> in the register's array, or null.</summary>
        private static unsafe byte* Axis(int handle)
        {
            IntPtr array;
            IL2CPP.il2cpp_field_static_get_value(axesField, &array);
            if (array == IntPtr.Zero || handle < 0)
                return null;
            // Il2CppArray: object header, bounds, length, then the elements
            long length = *(long*)((byte*)array + 3 * IntPtr.Size);
            return handle < length ? (byte*)array + 4 * IntPtr.Size + (long)handle * size : null;
        }

        /// <summary>Signed degrees from <paramref name="from"/> to <paramref name="to"/> around <paramref name="axis"/> only.</summary>
        private static float Twist(Vector3 from, Vector3 to, Vector3 axis) =>
            Vector3.SignedAngle(Vector3.ProjectOnPlane(from, axis), Vector3.ProjectOnPlane(to, axis), axis);

        /// <summary>
        /// Turns axis <paramref name="handle"/> back by however far the hull has
        /// turned around it since the last call.
        /// </summary>
        /// <param name="handle">The axis in the register.</param>
        /// <param name="part">What the axis turns.</param>
        /// <param name="axis">The axis, in world space.</param>
        /// <param name="limit">Hull speed (km/h) above which to leave the axis alone; 0 for none.</param>
        /// <param name="sign">Which way the axis angle turns the part around <paramref name="axis"/>, until seen otherwise.</param>
        /// <returns>The hull's speed in km/h.</returns>
        internal static unsafe float Stabilize(int handle, Transform part, Vector3 axis, float limit, float sign)
        {
            Transform? frame = part.parent;
            byte* p = Axis(handle);
            if (frame == null || p == null)
                return 0f;
            ref float current = ref *(float*)(p + angle);

            Vector3 direction = Mathf.Abs(Vector3.Dot(part.forward, axis)) < 0.9f ? part.forward : part.up;
            Vector3 local = frame.InverseTransformDirection(direction);
            Quaternion hull = part.root.rotation;
            Vector3 position = part.root.position;
            // Real time, so calls from FixedUpdate (whose game time lags the frame's) stay in order
            float now = Time.realtimeSinceStartup;
            float before = current;

            if (states.TryGetValue(handle, out State? s) && now - s.Time < 0.25f)
            {
                // How far the angle moved, and the part turned on its frame, since the last step
                float moved = Mathf.DeltaAngle(s.Angle, current);
                float turned = Twist(s.Local, local, frame.InverseTransformDirection(axis));
                // Check the sign over accumulated motion: one frame moves the part
                // hundredths of a degree, and frames without a physics step don't move it
                s.Moved += moved;
                s.Turned += turned;
                if (Mathf.Abs(s.Turned) > 0.5f)
                {
                    if (Mathf.Abs(s.Moved) > 0.25f && Mathf.Sign(s.Turned * s.Moved) != s.Gain)
                    {
                        s.Gain = Mathf.Sign(s.Turned * s.Moved);
                        Plugin.Logger.LogInfo($"Axis {handle} turns the other way: {s.Gain:+0;-0}° per degree of angle");
                    }
                    s.Moved = s.Turned = 0f;
                }

                s.Window += (now - s.Time) * Time.timeScale;

                float hullTurn = Twist(direction, hull * Quaternion.Inverse(s.Hull) * direction, axis);
                if (Mathf.Abs(hullTurn) < 20f && (limit <= 0f || s.Speed <= limit))
                {
                    current -= hullTurn / s.Gain;
                    float lo = *(float*)(p + min), hi = *(float*)(p + max);
                    if (hi > lo && hi - lo < 359f)
                        current = Mathf.Clamp(current, lo, hi);
                }

                // Speed over at least a few milliseconds: a correction just before firing
                // can come moments after the frame's, and dividing by that would blow up
                if (s.Window >= 0.01f)
                {
                    s.Speed = (position - s.WindowPosition).magnitude / s.Window * 3.6f;
                    s.Window = 0f;
                    s.WindowPosition = position;
                }
            }
            else
            {
                states[handle] = s = new State { Gain = sign, WindowPosition = position };
            }

            // The angle before correcting, so the next step's comparison includes the correction on both sides
            s.Hull = hull;
            s.Angle = before;
            s.Local = local;
            s.Time = now;
            return s.Speed;
        }
    }

    /// <summary>Behaviours of the stabilized drives, set as each drive's behaviour starts.</summary>
    [HarmonyPatch]
    internal static class Drives
    {
        // Behaviour, its speed limit (km/h, 0 for none)
        internal static readonly Dictionary<IntPtr, float> Stabilized = new();

        // Behaviour, what it turns, its HUD name, hull speed, speed limit and when it last ran
        internal static readonly Dictionary<IntPtr, (Transform Part, string Name, float Speed, float Limit, float Time)> Status = new();

        // Behaviour, every stabilized turret seen aiming and when, for re-stabilizing before its guns fire
        internal static readonly Dictionary<IntPtr, (TurretBehaviour Turret, float Time)> Turrets = new();

        private static readonly List<IntPtr> dead = new();

        /// <summary>
        /// Drops drives whose part is destroyed, with their vehicle, and the
        /// status of drives not run for a while, so the lists don't grow each battle.
        /// </summary>
        internal static void Prune(float now)
        {
            dead.Clear();
            foreach (var (pointer, (part, _, _, _, seen)) in Status)
                if (part == null || now - seen > 10f)
                    dead.Add(pointer);
            foreach (IntPtr pointer in dead)
            {
                // Kept for a live drive that's only idle, or the game paused: only its run sets it again
                if (Status[pointer].Part == null)
                    Stabilized.Remove(pointer);
                Status.Remove(pointer);
            }
        }

        private static void Set(Il2CppObjectBase? behaviour, float? limit)
        {
            if (behaviour == null)
                return;
            if (limit is { } value)
                Stabilized[behaviour.Pointer] = value;
            else
                Stabilized.Remove(behaviour.Pointer);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(TraverseMotor), nameof(TraverseMotor.EnableBehaviour))]
        private static void Traverse(TraverseMotor __instance) =>
            Set(__instance.behaviour, Plugin.Limit(__instance.traverseBlueprintSlot?.Blueprint));

        [HarmonyPostfix, HarmonyPatch(typeof(LayingDrive), nameof(LayingDrive.EnableBehaviour))]
        private static void Laying(LayingDrive __instance) =>
            Set(__instance.behaviour, Plugin.Limit(__instance.blueprintSlot?.Blueprint));

        [HarmonyPostfix, HarmonyPatch(typeof(TraverseMotorEditor), nameof(TraverseMotorEditor.OnGUI))]
        private static void TraverseEditor(TraverseMotorEditor __instance, IGUILayout __0)
        {
            var slot = __instance.Component?.TraverseBlueprintSlot;
            Plugin.Draw(__0, slot?.Blueprint, () => slot?.MarkModified(), __instance.RequestRedraw);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(LayingDriveEditor), nameof(LayingDriveEditor.OnGUI))]
        private static void LayingEditor(LayingDriveEditor __instance, IGUILayout __0)
        {
            var slot = __instance.Component?.BlueprintSlot;
            Plugin.Draw(__0, slot?.Blueprint, () => slot?.MarkModified(), __instance.RequestRedraw);
        }
    }

    /// <summary>Stabilizes turret traverse.</summary>
    [HarmonyPatch(typeof(TurretBehaviour), nameof(TurretBehaviour.MoveToTarget))]
    internal static class TurretMovePatch
    {
        private static void Prefix(TurretBehaviour __instance) => Run(__instance);

        internal static void Run(TurretBehaviour __instance)
        {
            using var _ = MadiLib.Profile.Measure("stabilizer: turret");
            Transform? turret = __instance.transform;
            if (turret?.parent == null || !Drives.Stabilized.TryGetValue(__instance.Pointer, out float limit))
                return;
            Drives.Turrets[__instance.Pointer] = (__instance, Time.realtimeSinceStartup);
            if (!Plugin.Enabled(turret))
            {
                Drives.Status[__instance.Pointer] = (turret, "traverse", 0f, limit, Time.realtimeSinceStartup);
                return;
            }
            float speed = Axes.Stabilize(__instance.physicsHandle, turret, turret.parent.TransformDirection(__instance.axis), limit, 1f);
            Drives.Status[__instance.Pointer] = (turret, "traverse", speed, limit, Time.realtimeSinceStartup);
            // The drive only applies its angle to the model while it's moving; keep model and angle together
            __instance.Apply();
        }
    }

    /// <summary>Stabilizes laying drive elevation and azimuth.</summary>
    [HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.MoveToTarget))]
    internal static class LayingDriveMovePatch
    {
        private static void Prefix(LayingDriveBehaviour __instance) => Run(__instance);

        internal static void Run(LayingDriveBehaviour __instance)
        {
            using var _ = MadiLib.Profile.Measure("stabilizer: laying drive");
            Transform? trunnions = __instance.trunnions;
            if (trunnions?.parent == null || !Drives.Stabilized.TryGetValue(__instance.Pointer, out float limit))
                return;
            float speed = 0f;
            string name = __instance.AzimuthLocked ? "elevation" : __instance.ElevationLocked ? "gun azimuth" : "gun";
            if (!Plugin.Enabled(trunnions))
            {
                Drives.Status[__instance.Pointer] = (trunnions, name, speed, limit, Time.realtimeSinceStartup);
                return;
            }
            if (!__instance.ElevationLocked)
                speed = Axes.Stabilize(__instance.elevationHandle, trunnions, trunnions.right, limit, -1f);
            if (!__instance.AzimuthLocked)
                speed = Axes.Stabilize(__instance.azimuthHandle, trunnions, trunnions.parent.rotation * __instance.zeroLocalRot * Vector3.up, limit, 1f);
            Drives.Status[__instance.Pointer] = (trunnions, name, speed, limit, Time.realtimeSinceStartup);
            // The drive only applies its angles to the model while it's moving; keep model and angles together
            __instance.Apply();
        }
    }

    /// <summary>
    /// Stabilizes a cannon's drives again just before it fires.
    /// </summary>
    /// <remarks>
    /// Drives are stabilized once a frame, but the gun fires after the hull's
    /// next physics step; without this the shell leaves one step of hull
    /// rotation behind the aim, wide in the direction of spin.
    /// </remarks>
    [HarmonyPatch(typeof(CannonBehaviour), nameof(CannonBehaviour.FireInternal))]
    internal static class FirePatch
    {
        private static void Prefix(CannonBehaviour __instance)
        {
            using var _ = MadiLib.Profile.Measure("stabilizer: fire prefix");
            if (Drives.Stabilized.Count == 0 || __instance.mount?.TryCast<Cannon>() is not { } cannon)
                return;
            Il2CppObjectBase? horizontal = cannon.HorizontalAimingMechanism, vertical = cannon.VerticalAimingMechanism;
            // The turret the gun sits on first, then the gun's own drives: the gun's mount may be a laying
            // drive with azimuth locked, so its aiming drives don't include the turret holding its heading
            Transform? gun = __instance.transform;
            float now = Time.realtimeSinceStartup;
            // A copy: running a turret updates its entry
            foreach (var (pointer, (turret, seen)) in new List<KeyValuePair<IntPtr, (TurretBehaviour, float)>>(Drives.Turrets))
                if (now - seen > 10f)
                    Drives.Turrets.Remove(pointer);
                else if (gun != null && now - seen < 1f && Drives.Stabilized.ContainsKey(pointer)
                    && turret.transform != null && gun.IsChildOf(turret.transform))
                    TurretMovePatch.Run(turret);
            Run(horizontal);
            if (vertical?.Pointer != horizontal?.Pointer)
                Run(vertical);
        }

        private static void Run(Il2CppObjectBase? drive)
        {
            if (drive?.TryCast<TurretBehaviour>() is { } turret)
                TurretMovePatch.Run(turret);
            else if (drive?.TryCast<LayingDriveBehaviour>() is { } laying)
                LayingDriveMovePatch.Run(laying);
        }
    }
}
