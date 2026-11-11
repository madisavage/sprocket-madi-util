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
using Physics = UnityPhysicsModule::UnityEngine.Physics;
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
            Keybinds.Register(this, "Fire control", "Raise gun", "WheelUp", () => Aim.Adjust(1));
            Keybinds.Register(this, "Fire control", "Lower gun", "WheelDown", () => Aim.Adjust(-1));
            Keybinds.Register(this, "Fire control", "Range", "T", Aim.Range);
            Keybinds.Register(this, "Fire control", "Reset elevation", "Alt+T", Aim.Reset);
            SettingsTab.AddSection("Fire control", "Fire control", ManualStep, StereoBase, StereoError, StereoTime);
            Hud.AddPanel(Aim.Status);
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

        internal static Rangefinder RangefinderOf(CannonBlueprint? gun) =>
            Read(gun)?["rangefinder"] is JsonValue value && value.TryGetValue(out string? name) && Enum.TryParse(name, true, out Rangefinder kind) ? kind : Rangefinder.None;

        internal static float ElevationOf(CannonBlueprint? gun) =>
            Read(gun)?["elevation"] is JsonValue value && value.TryGetValue(out float mils) && float.IsFinite(mils) ? mils : 0f;

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

        // Mils by hand, on top of the ranged elevation
        private static float manual;
        // Last reading (m), and when a stereo reading in progress lands
        private static float? range;
        private static float pendingRange, pendingAt = float.NaN;
        // The aim ray as the sight gave it, before raising
        private static Ray sight;
        private static Transform? hull;
        private static Cannon? gun;
        private static float lookedUp;
        // Per cannon: range, speed and projectile its elevation was last worked out for, and the result (radians)
        private static readonly Dictionary<IntPtr, ((float Range, float Speed, IntPtr Projectile) For, float? Angle)> solutions = new();
        // Laying drive, the cannon on its trunnions
        private static readonly Dictionary<IntPtr, Cannon?> laid = new();
        // Cannon blueprint, its default elevation field
        private static readonly ObjectCache<FloatProperty> elevations = new();

        internal static void Adjust(int direction)
        {
            if (PlayerVehicle.Root != null)
                manual += direction * Plugin.ManualStep.Value;
        }

        internal static void Reset()
        {
            manual = 0f;
            solutions.Clear();
            laid.Clear();
            range = null;
            pendingAt = float.NaN;
        }

        /// <summary>Ranges the point under the crosshair with the gun's rangefinder.</summary>
        internal static void Range()
        {
            if (Gun() is not { } cannon || GunSettings.RangefinderOf(cannon.Blueprint) is var kind && kind == Rangefinder.None || hull == null)
                return;
            float nearest = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(sight, MaxRange))
                if (hit.distance < nearest && hit.transform != null && !hit.transform.IsChildOf(hull))
                    nearest = hit.distance;
            if (float.IsPositiveInfinity(nearest))
                return;
            if (kind == Rangefinder.Laser)
            {
                range = nearest;
                pendingAt = float.NaN;
                return;
            }
            // Stereo error grows with range squared and shrinks with base length
            float sigma = Plugin.StereoError.Value / 1000f * nearest * nearest / Plugin.StereoBase.Value;
            pendingRange = MathF.Max(1f, nearest + sigma * Gaussian());
            pendingAt = Time.time + Plugin.StereoTime.Value;
        }

        private static float Gaussian() =>
            MathF.Sqrt(-2f * MathF.Log(1f - (float)random.NextDouble())) * MathF.Cos(2f * MathF.PI * (float)random.NextDouble());

        internal static string? Status()
        {
            if (PlayerVehicle.Root == null || Gun() is not { } cannon)
                return null;
            var kind = GunSettings.RangefinderOf(cannon.Blueprint);
            float mils = GunSettings.ElevationOf(cannon.Blueprint) + manual;
            if (kind == Rangefinder.None && mils == 0f)
                return null;
            string ranged = !float.IsNaN(pendingAt) ? "ranging…"
                : range is float r ? $"{r:0} m" + (solutions.TryGetValue(cannon.Pointer, out var s) && s.Angle == null ? " (out of reach)" : "")
                : kind == Rangefinder.None ? "" : "no range";
            string extra = mils == 0f ? "" : $"{mils:+0.0;-0.0} mil";
            return $"Fire control: {ranged}{(ranged.Length > 0 && extra.Length > 0 ? ", " : "")}{extra}";
        }

        /// <summary>The player's gun with a rangefinder, else their first; looked up again on a new vehicle or each second while none.</summary>
        private static Cannon? Gun()
        {
            Transform? root = PlayerVehicle.Root;
            float now = Time.realtimeSinceStartup;
            if (root != hull || gun == null && now - lookedUp > 1f)
            {
                if (root != hull)
                    Reset();
                hull = root;
                lookedUp = now;
                gun = null;
                if (root != null)
                    foreach (Cannon cannon in root.GetComponentsInChildren<Cannon>())
                    {
                        gun ??= cannon;
                        if (GunSettings.RangefinderOf(cannon.Blueprint) != Rangefinder.None)
                        {
                            gun = cannon;
                            break;
                        }
                    }
            }
            return gun;
        }

        /// <summary>Radians to raise <paramref name="cannon"/> above the sight's aim now: its default, by hand, and for the range with its loaded round.</summary>
        private static float Elevation(Cannon? cannon)
        {
            if (!float.IsNaN(pendingAt) && Time.time >= pendingAt)
            {
                range = pendingRange;
                pendingAt = float.NaN;
            }
            if (cannon == null)
                return manual / 1000f;
            float angle = (GunSettings.ElevationOf(cannon.Blueprint) + manual) / 1000f;
            if (range is not float r || cannon.behaviour is not { } behaviour)
                return angle;
            // An unloaded gun's type is -1: keep the last solution
            if (behaviour.LoadedProjectileTypeID.Value >= 0
                && behaviour.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource?.GetProjectileDefinition(behaviour.LoadedProjectileTypeID) is { } projectile)
            {
                // rounds sets the cannon's muzzle velocity to its loaded round's
                var key = (r, cannon.muzzleVelocity, projectile.Pointer);
                if (!solutions.TryGetValue(cannon.Pointer, out var solution) || solution.For != key)
                {
                    float mass = projectile.Mass;
                    solutions[cannon.Pointer] = solution = (key, Ballistics.Elevation(cannon.muzzleVelocity, v => mass > 0f ? projectile.CalculateDragForce(v) / mass : 0f, r, -Physics.gravity.y));
                }
                return angle + (solution.Angle ?? 0f);
            }
            return angle + (solutions.TryGetValue(cannon.Pointer, out var last) ? last.Angle ?? 0f : 0f);
        }

        /// <summary>The sight's aim ray, for ranging.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(VehicleController), nameof(VehicleController.UpdateControl))]
        private static void Sight(Ray aimRay) => sight = aimRay;

        /// <summary>
        /// Raises the direction the player's laying drives lay the gun in; the sight's view keeps the player's aim.
        /// Raising the controller's aim ray instead did nothing: the gun is laid from elsewhere.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.AimAtDirection))]
        private static void Raise(LayingDriveBehaviour __instance, ref Vector3 worldDirection)
        {
            if (PlayerVehicle.Root is not { } root || __instance.trunnions is not { } trunnions || !trunnions.IsChildOf(root))
                return;
            if (!laid.TryGetValue(__instance.Pointer, out Cannon? cannon))
                laid[__instance.Pointer] = cannon = trunnions.GetComponentInChildren<Cannon>();
            float angle = Elevation(cannon);
            if (angle != 0f)
                worldDirection = Vector3.RotateTowards(worldDirection, Vector3.up, angle, 0f);
        }

        /// <summary>A Rangefinder dropdown and a Default elevation field near the top of the cannon panel, after madiUtil's Gun settings.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI)), HarmonyAfter("madi.madiutil")]
        private static void Draw(CannonEditor __instance, IGUILayout __0)
        {
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null || __instance.Component is not { } cannon || cannon.Blueprint is not { } blueprint)
                return;
            EditorGui.Dropdown(drawer, "Rangefinder", GunSettings.Names, (int)GunSettings.RangefinderOf(blueprint), i =>
            {
                if (GunSettings.Set(blueprint, "rangefinder", i == 0 ? null : ((Rangefinder)i).ToString().ToLowerInvariant()))
                    cannon.blueprintSlot?.MarkModified();
                __instance.RequestRedraw();
            }, "Ranges the point under the crosshair (Range key) and raises the gun for the loaded round. Stereoscopic: slower, and off by more at long range. Laser: instant and exact.");
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
