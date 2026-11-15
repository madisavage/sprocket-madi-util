extern alias UnityCoreModule;
extern alias UnityPhysicsModule;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using GUI = UnityEngine.GUI;
using Physics = UnityPhysicsModule::UnityEngine.Physics;
using Rect = UnityCoreModule::UnityEngine.Rect;
using Ray = UnityCoreModule::UnityEngine.Ray;
using Time = UnityCoreModule::UnityEngine.Time;
using Transform = UnityCoreModule::UnityEngine.Transform;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace FireControl
{
    /// <summary>
    /// Raises the player's gun above the sight's aim point: by hand in mils,
    /// and with a rangefinder picked on the gun, by the superelevation the
    /// loaded round needs at the ranged distance.
    /// </summary>
    [BepInPlugin("madi.firecontrol", "fireControl", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ConfigEntry<float> ManualStep = null!;
        internal static ConfigEntry<float> StereoBase = null!;
        internal static ConfigEntry<float> StereoError = null!;
        internal static ConfigEntry<float> StereoTime = null!;
        internal static ConfigEntry<float> LaserTime = null!;
        internal static ManualLogSource Logger = null!;

        public override void Load()
        {
            Logger = Log;
            ManualStep = Config.Bind("Elevation", "Step", 0.5f, new ConfigDescription(
                "Mils the gun goes up or down per press of Raise gun / Lower gun.",
                new AcceptableValueRange<float>(0.05f, 10f), new SoftMax(2f), new Step(0.05f)));
            StereoBase = Config.Bind("Rangefinder", "Stereoscopic base length", 1f, new ConfigDescription(
                "Metres between a stereoscopic rangefinder's lenses. Longer is more precise.",
                new AcceptableValueRange<float>(0.2f, 5f), new SoftMax(3f), new Step(0.1f)));
            StereoError = Config.Bind("Rangefinder", "Stereoscopic error", 0.03f, new ConfigDescription(
                "The gunner's matching error, in mils: a reading is off by about this × range² ÷ base. 0.03 is ±30 m at 1 km with a 1 m base.",
                new AcceptableValueRange<float>(0f, 1f), new SoftMax(0.2f), new Step(0.005f)));
            StereoTime = Config.Bind("Rangefinder", "Stereoscopic ranging time", 4f, new ConfigDescription(
                "Seconds a stereoscopic reading takes.",
                new AcceptableValueRange<float>(0f, 30f), new SoftMax(10f), new Step(0.5f)));
            LaserTime = Config.Bind("Rangefinder", "Laser ranging time", 0.3f, new ConfigDescription(
                "Seconds a laser reading takes.",
                new AcceptableValueRange<float>(0f, 5f), new SoftMax(1f), new Step(0.1f)));
            Keybinds.Register(this, "Fire control", "Raise gun", "WheelUp", () => Aim.Adjust(1));
            Keybinds.Register(this, "Fire control", "Lower gun", "WheelDown", () => Aim.Adjust(-1));
            Keybinds.Register(this, "Fire control", "Range", "T", Aim.Range);
            Keybinds.Register(this, "Fire control", "Reset elevation", "Alt+T", Aim.Reset);
            SettingsTab.AddSection("Fire control", "Fire control", ManualStep, StereoBase, StereoError, StereoTime, LaserTime);
            Hud.AddPanel(Aim.Status);
            Hud.AddPanel(Aim.Progress);
            var harmony = new Harmony("madi.firecontrol");
            harmony.PatchAll();
            Logger.LogInfo($"Patched: {string.Join(", ", System.Linq.Enumerable.Select(harmony.GetPatchedMethods(), m => $"{m.DeclaringType?.Name}.{m.Name}"))}");
        }
    }

    internal enum Rangefinder { None, Stereoscopic, Laser }

    /// <summary>
    /// A cannon's fire control settings, kept in its blueprint's JSON description:
    /// <c>"rangefinder"</c> and <c>"elevation"</c> (default mils above the sight, e.g. for a missile's climb).
    /// </summary>
    internal static class GunSettings
    {
        internal static readonly string[] Names = { "None", "Stereoscopic", "Laser" };
        private static JsonObject? Read(CannonBlueprint? gun) => gun == null ? null : Descriptions.Parsed(gun);

        internal static Rangefinder RangefinderOf(CannonBlueprint? gun)
        {
            JsonValue? value = Read(gun)?["rangefinder"] as JsonValue;
            if (value == null)
                return Rangefinder.None;
            if (!value.TryGetValue(out string? name))
                return Rangefinder.None;
            if (!Enum.TryParse(name, true, out Rangefinder kind))
                return Rangefinder.None;
            return kind;
        }

        internal static float ElevationOf(CannonBlueprint? gun)
        {
            JsonValue? value = Read(gun)?["elevation"] as JsonValue;
            if (value == null)
                return 0f;
            if (!value.TryGetValue(out float mils))
                return 0f;
            // A hand-edited NaN or infinity would throw the gun's aim away
            if (!float.IsFinite(mils))
                return 0f;
            return mils;
        }

        /// <summary>Sets <paramref name="key"/> in <paramref name="gun"/>'s description (removed for null), other keys kept; false when it holds other text than JSON.</summary>
        internal static bool Set(CannonBlueprint gun, string key, JsonNode? value)
        {
            if (Descriptions.Set(gun, key, value))
                return true;
            Plugin.Logger.LogWarning($"Cannon {gun.Name}'s description isn't JSON, so its fire control settings can't be saved.");
            return false;
        }
    }

    /// <summary>The player's superelevation: by hand, and from the last range reading.</summary>
    // HarmonyPatch on the class: PatchAll only looks at patch methods in classes that have it
    [HarmonyPatch]
    internal static class Aim
    {
        private const float MaxRange = 8000f;
        private static readonly Random random = new();

        // Per cannon: mils by hand, on top of the ranged elevation, and its last reading (m)
        private static readonly Dictionary<IntPtr, float> manual = new(), ranges = new();
        // The cannon a reading in progress is for, the reading, and when it started and lands
        private static IntPtr pendingGun;
        private static float pendingRange, pendingFrom, pendingAt = float.NaN;
        // The aim ray as the sight gave it, before raising, and the player's controller (its fire group picks the gun)
        private static Ray sight;
        private static VehicleController? controller;
        private static Transform? hull;
        // The player's guns and their fire groups (flags, as the controller's)
        private static readonly List<(Cannon Cannon, int Group)> guns = new();
        private static float lookedUp;
        // Per cannon: range, speed and projectile its elevation was last worked out for, and the result (radians)
        private static readonly Dictionary<IntPtr, ((float Range, float Speed, IntPtr Projectile) For, float? Angle)> solutions = new();
        // Laying drive, the cannon on its trunnions
        private static readonly Dictionary<IntPtr, Cannon?> laid = new();
        // Cannon blueprint, its default elevation field
        private static readonly ObjectCache<FloatProperty> elevations = new();

        internal static void Adjust(int direction)
        {
            Cannon? cannon = Gun();
            if (cannon != null)
                manual[cannon.Pointer] = manual.GetValueOrDefault(cannon.Pointer) + direction * Plugin.ManualStep.Value;
        }

        /// <summary>Clears the active gun's by-hand mils and range.</summary>
        internal static void Reset()
        {
            Cannon? cannon = Gun();
            if (cannon == null)
                return;
            manual.Remove(cannon.Pointer);
            ranges.Remove(cannon.Pointer);
            if (pendingGun == cannon.Pointer)
                pendingAt = float.NaN;
        }

        /// <summary>Forgets every gun's settings, for a new vehicle.</summary>
        private static void Clear()
        {
            manual.Clear();
            ranges.Clear();
            solutions.Clear();
            laid.Clear();
            guns.Clear();
            pendingAt = float.NaN;
        }

        /// <summary>Lands a reading in progress once its time is up.</summary>
        private static void Land()
        {
            if (!float.IsNaN(pendingAt) && Time.time >= pendingAt)
            {
                ranges[pendingGun] = pendingRange;
                pendingAt = float.NaN;
            }
        }

        /// <summary>Ranges the point under the crosshair with the gun's rangefinder.</summary>
        internal static void Range()
        {
            Cannon? cannon = Gun();
            if (cannon == null)
                return;
            Rangefinder kind = GunSettings.RangefinderOf(cannon.Blueprint);
            if (kind == Rangefinder.None)
                return;
            if (hull == null)
                return;
            float nearest = float.PositiveInfinity;
            foreach (UnityPhysicsModule::UnityEngine.RaycastHit hit in Physics.RaycastAll(sight, MaxRange))
            {
                if (hit.distance >= nearest)
                    continue;
                if (hit.transform == null)
                    continue;
                // The player's own vehicle
                if (hit.transform.IsChildOf(hull))
                    continue;
                nearest = hit.distance;
            }
            if (float.IsPositiveInfinity(nearest))
                return;
            // Stereo error grows with range squared and shrinks with base length; laser is exact
            float sigma = kind == Rangefinder.Laser ? 0f : Plugin.StereoError.Value / 1000f * nearest * nearest / Plugin.StereoBase.Value;
            pendingGun = cannon.Pointer;
            pendingRange = MathF.Max(1f, nearest + sigma * Gaussian());
            pendingFrom = Time.time;
            pendingAt = pendingFrom + (kind == Rangefinder.Laser ? Plugin.LaserTime : Plugin.StereoTime).Value;
        }

        /// <summary>A bar over the status panel filling up while a reading is in progress.</summary>
        internal static float Progress(float left, float bottom)
        {
            if (float.IsNaN(pendingAt) || PlayerVehicle.Root == null || pendingAt <= pendingFrom)
                return 0f;
            const float width = 160f, height = 26f, pad = 5f;
            GUI.Box(new Rect(left, bottom - height, width, height), "");
            float remaining = MathF.Max(0f, pendingAt - Time.time);
            HudDraw.Bar(new Rect(left + pad, bottom - height + pad, width - 2 * pad, height - 2 * pad), 1f - remaining / (pendingAt - pendingFrom), HudDraw.Next);
            HudDraw.Label(new Rect(left + pad, bottom - height + pad, width - 2 * pad, height - 2 * pad), $"Ranging {remaining:0.0} s", 13, HudDraw.Idle);
            return height;
        }

        private static float Gaussian() =>
            MathF.Sqrt(-2f * MathF.Log(1f - (float)random.NextDouble())) * MathF.Cos(2f * MathF.PI * (float)random.NextDouble());

        internal static string? Status()
        {
            if (PlayerVehicle.Root == null)
                return null;
            Cannon? cannon = Gun();
            if (cannon == null)
                return null;
            Land();
            Rangefinder kind = GunSettings.RangefinderOf(cannon.Blueprint);
            float mils = GunSettings.ElevationOf(cannon.Blueprint) + manual.GetValueOrDefault(cannon.Pointer);
            if (kind == Rangefinder.None && mils == 0f)
                return null;
            string ranged;
            if (!float.IsNaN(pendingAt) && pendingGun == cannon.Pointer)
                ranged = "ranging…";
            else if (ranges.TryGetValue(cannon.Pointer, out float r))
            {
                ranged = $"{r:0} m";
                // Ranged, but too far for the gun to reach
                if (solutions.TryGetValue(cannon.Pointer, out ((float Range, float Speed, IntPtr Projectile) For, float? Angle) s) && s.Angle == null)
                    ranged += " (out of reach)";
            }
            else if (kind == Rangefinder.None)
                ranged = "";
            else
                ranged = "no range";
            string extra = mils == 0f ? "" : $"{mils:+0.0;-0.0} mil";
            string separator = "";
            if (ranged.Length > 0 && extra.Length > 0)
                separator = ", ";
            return $"Fire control: {ranged}{separator}{extra}";
        }

        /// <summary>
        /// The gun the player is using: in the controller's fire group, one with a
        /// rangefinder first; else the player's first gun. The guns are looked up
        /// again on a new vehicle or each second while none.
        /// </summary>
        private static Cannon? Gun()
        {
            Transform? root = PlayerVehicle.Root;
            float now = Time.realtimeSinceStartup;
            // A new vehicle, or none found yet and a second since the last look
            bool lookAgain = root != hull;
            if (guns.Count == 0 && now - lookedUp > 1f)
                lookAgain = true;
            if (lookAgain)
            {
                if (root != hull)
                    Clear();
                hull = root;
                lookedUp = now;
                guns.Clear();
                if (root != null)
                    foreach (Cannon cannon in root.GetComponentsInChildren<Cannon>())
                        guns.Add((cannon, cannon.Cast<IFiregroupUser>().FireGroup));
            }
            if (guns.Count == 0)
                return null;
            int group = controller == null ? 0 : (int)controller.FireGroup;
            Cannon? pick = null;
            foreach ((Cannon cannon, int cannonGroup) in guns)
            {
                if (cannon == null)
                    continue;
                // Not in the controller's fire group; group 0 is no controller, so any gun
                if (group != 0 && (cannonGroup & group) == 0)
                    continue;
                if (GunSettings.RangefinderOf(cannon.Blueprint) != Rangefinder.None)
                    return cannon;
                pick ??= cannon;
            }
            return pick ?? guns[0].Cannon;
        }

        /// <summary>Radians to raise <paramref name="cannon"/> above the sight's aim now: its default, by hand, and for the range with its loaded round.</summary>
        private static float Elevation(Cannon cannon)
        {
            Land();
            float angle = (GunSettings.ElevationOf(cannon.Blueprint) + manual.GetValueOrDefault(cannon.Pointer)) / 1000f;
            if (!ranges.TryGetValue(cannon.Pointer, out float r))
                return angle;
            Sprocket.Vehicles.Weapons.Cannons.CannonBehaviour? behaviour = cannon.behaviour;
            if (behaviour == null)
                return angle;
            ProjectileDefinition? projectile = null;
            // An unloaded gun's type is -1: keep the last solution
            if (behaviour.LoadedProjectileTypeID.Value >= 0)
                projectile = behaviour.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource?.GetProjectileDefinition(behaviour.LoadedProjectileTypeID);
            if (projectile == null)
            {
                if (solutions.TryGetValue(cannon.Pointer, out ((float Range, float Speed, IntPtr Projectile) For, float? Angle) last))
                    return angle + (last.Angle ?? 0f);
                return angle;
            }
            // rounds sets the cannon's muzzle velocity to its loaded round's
            (float Range, float Speed, IntPtr Projectile) key = (r, cannon.muzzleVelocity, projectile.Pointer);
            if (!solutions.TryGetValue(cannon.Pointer, out ((float Range, float Speed, IntPtr Projectile) For, float? Angle) solution) || solution.For != key)
            {
                float mass = projectile.Mass;
                float? solved = Ballistics.Elevation(cannon.muzzleVelocity, v => mass > 0f ? projectile.CalculateDragForce(v) / mass : 0f, r, -Physics.gravity.y);
                solution = (key, solved);
                solutions[cannon.Pointer] = solution;
            }
            return angle + (solution.Angle ?? 0f);
        }

        /// <summary>The sight's aim ray, for ranging, and the controller, for its fire group.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(VehicleController), nameof(VehicleController.UpdateControl))]
        private static void Sight(VehicleController __instance, Ray aimRay)
        {
            sight = aimRay;
            controller = __instance;
        }

        /// <summary>
        /// Raises the direction the player's laying drives lay the gun in; the sight's view keeps the player's aim.
        /// Raising the controller's aim ray instead did nothing: the gun is laid from elsewhere.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.AimAtDirection))]
        private static void Raise(LayingDriveBehaviour __instance, ref Vector3 worldDirection)
        {
            using var _ = Profile.Measure("fireControl: raise laying");
            Transform? root = PlayerVehicle.Root;
            if (root == null)
                return;
            Transform? trunnions = __instance.trunnions;
            if (trunnions == null)
                return;
            // Another vehicle's drive
            if (!trunnions.IsChildOf(root))
                return;
            if (!laid.TryGetValue(__instance.Pointer, out Cannon? cannon))
            {
                cannon = trunnions.GetComponentInChildren<Cannon>();
                laid[__instance.Pointer] = cannon;
            }
            // Each gun keeps its own by-hand mils and range
            float angle = cannon == null ? 0f : Elevation(cannon);
            if (angle != 0f)
                worldDirection = Vector3.RotateTowards(worldDirection, Vector3.up, angle, 0f);
        }

        /// <summary>A Rangefinder dropdown and a Default elevation field near the top of the cannon panel, after madiUtil's Gun settings.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI)), HarmonyAfter("madi.madiutil")]
        private static void Draw(CannonEditor __instance, IGUILayout __0)
        {
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null)
                return;
            Cannon? cannon = __instance.Component;
            if (cannon == null)
                return;
            CannonBlueprint? blueprint = cannon.Blueprint;
            if (blueprint == null)
                return;
            EditorGui.Dropdown(drawer, "Rangefinder", GunSettings.Names, (int)GunSettings.RangefinderOf(blueprint), i =>
            {
                if (GunSettings.Set(blueprint, "rangefinder", i == 0 ? null : ((Rangefinder)i).ToString().ToLowerInvariant()))
                    cannon.blueprintSlot?.MarkModified();
                __instance.RequestRedraw();
            }, "Ranges the point under the crosshair (Range key) and raises the gun for the loaded round. Stereoscopic: slower, and off by more at long range. Laser: quick and exact. The ranging times are in the settings.");
            drawer.Float(elevations.Get(blueprint, () => new FloatProperty(
                "Default elevation",
                (Func<float>)(() => GunSettings.ElevationOf(blueprint)),
                (Action<float>)(value =>
                {
                    float mils = MathF.Round(value * 10f) / 10f;
                    if (GunSettings.Set(blueprint, "elevation", mils == 0f ? null : mils))
                        cannon.blueprintSlot?.MarkModified();
                }),
                "Mils the gun always sits above the sight's aim, before any by hand or for range: e.g. a guided missile's climb above the sight line. Saved with the vehicle.")
            {
                Min = -50f,
                Max = 300f,
                SoftMin = -10f,
                SoftMax = 50f,
                UseSoftLimits = true,
                Step = 0.5f
            }));
        }
    }
}
