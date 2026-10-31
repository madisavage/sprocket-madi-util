extern alias UnityCoreModule;

extern alias UnityPhysicsModule;

using System;
using System.Text.RegularExpressions;
using Properties;
using HarmonyLib;
using MadiLib;
using Sprocket.ContinuousTracks;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.UI;
using Sprocket.VehicleControl;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Tracks;
using Mathf = UnityCoreModule::UnityEngine.Mathf;
using ForceMode = UnityPhysicsModule::UnityEngine.ForceMode;
using Physics = UnityPhysicsModule::UnityEngine.Physics;
using Rigidbody = UnityPhysicsModule::UnityEngine.Rigidbody;
using Time = UnityCoreModule::UnityEngine.Time;
using Transform = UnityCoreModule::UnityEngine.Transform;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;
using NVector3 = System.Numerics.Vector3;

namespace Hydropneumatic
{
    /// <summary>
    /// Hull aiming: a cannon tagged <c>[hullaim]</c> on the player's vehicle is
    /// aimed by turning the hull with its tracks and pitching it on its suspension.
    /// </summary>
    /// <remarks>
    /// The hull turns through the player's own steer input, only while the
    /// player isn't steering, so the game's tracks do the turning and any key
    /// wins. It pitches through <see cref="Plugin.PitchTarget"/>. The gun is
    /// aimed at what's under the crosshair: the game doesn't lay a gun that
    /// has no drives of its own. Pure parts (tag, angles, steer controller)
    /// are unit-tested.
    /// </remarks>
    [HarmonyPatch]
    internal static class HullAim
    {
        // [hullaim], [hullaim 3] for a 3 deg/s laying drive, [hullaim 3 60] for 60% of the weight on each track as its force too
        private static readonly Regex tag = new(@"\s*\[hullaim(?:\s+([0-9]*\.?[0-9]+))?(?:\s+([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);
        // Any [hullaim ...], to tell a bad tag from none
        private static readonly Regex anyTag = new(@"\[hullaim\b[^\]]*\]", RegexOptions.IgnoreCase);
        internal const float DefaultRate = 3f, DefaultForce = 60f;

        // Steer controller: output per degree of yaw error, per degree/s of yaw rate,
        // and how fast the output may change (per second)
        private const float Kp = 0.1f, Kd = 0.05f, SlewRate = 4f;
        // Starts turning past StartAngle degrees and keeps on until within StopAngle, at least MinSteer meanwhile:
        // small steer barely turns a tracked hull, and each stop lets the engine idle down. Inside, the laying drive aims
        private const float StartAngle = 2f, StopAngle = 1f, MinSteer = 0.25f;
        // Laying drive: wanted yaw rate (degrees/s) per degree of error, at most the tag's rate;
        // yaw acceleration (rad/s²) per degree/s of rate error, and per degree of its integral
        private const float LayKp = 2f, LayKv = 20f, LayKi = 400f;

        /// <summary>
        /// The laying drive's top rate (degrees/s) and force (% of the weight on each track) in
        /// <paramref name="name"/>'s <c>[hullaim]</c> tag, defaults for missing numbers; null when untagged or the tag doesn't parse.
        /// </summary>
        internal static (float Rate, float Force)? Settings(string? name) => Plugin.Numbers(tag, anyTag, name, DefaultRate, DefaultForce);

        /// <summary><paramref name="name"/> with its tag replaced by one for <paramref name="settings"/>, or removed for null.</summary>
        internal static string WithSettings(string name, (float Rate, float Force)? settings) => Plugin.WithNumbers(tag, "hullaim", name, settings);

        private const float Deg = MathF.PI / 180f;

        /// <summary>Signed degrees from <paramref name="from"/> to <paramref name="to"/> around <paramref name="axis"/>, as Unity's <c>SignedAngle</c>.</summary>
        private static float Signed(NVector3 from, NVector3 to, NVector3 axis)
        {
            from -= axis * NVector3.Dot(from, axis);
            to -= axis * NVector3.Dot(to, axis);
            return MathF.Atan2(NVector3.Dot(axis, NVector3.Cross(from, to)), NVector3.Dot(from, to)) / Deg;
        }

        /// <summary>Degrees the gun must turn right (positive) to point along <paramref name="desired"/>, around the hull's <paramref name="up"/>.</summary>
        internal static float Yaw(NVector3 forward, NVector3 desired, NVector3 up) => Signed(forward, desired, NVector3.Normalize(up));

        /// <summary>Degrees the gun must pitch up (positive) to point along <paramref name="desired"/>, around the hull's <paramref name="right"/>.</summary>
        // Turning about +right tips the nose down, so up is the negative angle
        internal static float Pitch(NVector3 forward, NVector3 desired, NVector3 right) => -Signed(forward, desired, NVector3.Normalize(right));

        /// <summary>
        /// The steer input (−1 left … 1 right) that turns the hull onto its aim:
        /// proportional to <paramref name="yawError"/> (degrees), less the yaw
        /// rate (degrees/s, right positive) to damp it, changed from
        /// <paramref name="previous"/> at most <see cref="SlewRate"/> per second.
        /// </summary>
        /// <remarks>
        /// A turn starts past <see cref="StartAngle"/> degrees and goes on, at least
        /// <see cref="MinSteer"/> toward the aim, until within <see cref="StopAngle"/>:
        /// one steady turn per aim change rather than pulses the engine spins up for.
        /// </remarks>
        /// <param name="turning">Whether the last call was turning.</param>
        internal static (float Steer, bool Turning) Steer(float yawError, float yawRate, float previous, float dt, bool turning)
        {
            float error = MathF.Abs(yawError);
            turning = turning ? error > StopAngle : error > StartAngle;
            float wanted = 0f;
            if (turning)
            {
                wanted = Math.Clamp(Kp * yawError - Kd * yawRate, -1f, 1f);
                // Toward the aim at least the minimum; damping may still ease it, never reverse it
                if (MathF.Sign(wanted) != -MathF.Sign(yawError))
                    wanted = MathF.Sign(yawError) * MathF.Max(MinSteer, MathF.Abs(wanted));
            }
            return (previous + Math.Clamp(wanted - previous, -SlewRate * dt, SlewRate * dt), turning);
        }

        /// <summary>
        /// The laying drive's yaw acceleration (rad/s², right positive) for
        /// <paramref name="yawError"/> (degrees) and <paramref name="yawRate"/>
        /// (degrees/s), and its rate-error integral (degrees) carried from <paramref name="integral"/>.
        /// </summary>
        /// <remarks>
        /// The engine can't lay the hull finely: it spools up and turns in steps.
        /// So within <see cref="StopAngle"/> of the aim a separate drive does, as
        /// the Strv 103's hydraulic steering did: forces along the tracks, not the engine.
        /// It controls yaw rate, and the integral grows until the hull breaks free
        /// of the tracks' friction, which is far more than its inertia needs.
        /// </remarks>
        /// <param name="maxRate">The fastest it lays, in degrees/s.</param>
        /// <param name="capped">Whether last step's force hit the grip cap: the integral holds then.</param>
        internal static (float Accel, float Integral) Lay(float yawError, float yawRate, float integral, float dt, float maxRate, bool capped)
        {
            float rateError = Math.Clamp(LayKp * yawError, -maxRate, maxRate) - yawRate;
            if (!capped)
                integral += rateError * dt;
            return ((LayKv * rateError + LayKi * integral) * Deg, integral);
        }

        // Seconds over which the pitch command closes the barrel's pitch error: slower than the struts follow it
        private const float PitchSmoothing = 0.5f;

        // Player's steer input below this counts as not steering
        private const float Steering = 0.05f;
        // Aim point when nothing is under the crosshair, and the farthest hit looked for, in metres
        private const float SkyDistance = 2000f, MaxRange = 5000f;

        /// <summary>Whether hull aiming is on; the keybind flips it.</summary>
        internal static bool Enabled = true;

        /// <summary>Whether the player's vehicle has a tagged gun, as of the last second.</summary>
        internal static bool Active => Time.realtimeSinceStartup - seen < 1f;

        private static Transform? root;
        private static Cannon? gun;
        private static float seen, command, lookedUp, aimed, layError = float.NaN, layIntegral, layForce, layHold, layRate, layGrip;
        private static bool steering, turning, failed, leftContact, rightContact, layCapped;

        /// <summary>Flips hull aiming; off hands pitch back to the keys.</summary>
        internal static void Toggle()
        {
            Enabled = !Enabled;
            Stop();
        }

        private static void Stop()
        {
            command = 0f;
            layError = float.NaN;
            Plugin.PitchTarget = null;
        }

        private static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);

        /// <summary>The player's first gun tagged <c>[hullaim]</c>, looked up again when the vehicle changes.</summary>
        private static Cannon? Gun(Transform hull)
        {
            // Again each second while none is found: a vehicle's guns can appear after it does
            float now = Time.realtimeSinceStartup;
            if (hull != root || gun == null && now - lookedUp > 1f)
            {
                root = hull;
                lookedUp = now;
                layHold = 0f;
                gun = null;
                foreach (Cannon cannon in hull.GetComponentsInChildren<Cannon>())
                    if (Settings(cannon.Blueprint?.Name) is var (rate, force))
                    {
                        gun = cannon;
                        layRate = rate;
                        layGrip = force / 100f;
                        break;
                    }
            }
            return gun;
        }

        /// <summary>
        /// Where the player aims: the nearest hit along the aim ray that isn't
        /// the player's own vehicle, or a point far along it.
        /// </summary>
        private static Vector3 Target(UnityCoreModule::UnityEngine.Ray ray, Transform hull)
        {
            float nearest = SkyDistance;
            foreach (var hit in Physics.RaycastAll(ray, MaxRange))
                if (hit.distance < nearest && hit.transform != null && !hit.transform.IsChildOf(hull))
                    nearest = hit.distance;
            return ray.origin + ray.direction * nearest;
        }

        /// <summary>Each frame of the player's control: pitch toward the aim, and the steer to turn onto it.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(VehicleController), nameof(VehicleController.UpdateControl))]
        private static void Aim(VehicleController __instance)
        {
            if (failed)
                return;
            using var _ = MadiLib.Profile.Measure("hydropneumatic: hull aiming");
            layError = float.NaN;
            try
            {
                if (PlayerVehicle.Root is not { } hull || Gun(hull) is not { } cannon || cannon == null || cannon.transform is not { } barrel)
                {
                    command = 0f;
                    return;
                }
                seen = Time.realtimeSinceStartup;
                if (!Enabled)
                    return;
                if (__instance.GunnerAimLocked)
                {
                    // The game's "lock turret" held: look around freely; the hull stays and the pitch holds
                    command = 0f;
                    turning = false;
                    Plugin.PitchTarget = null;
                    return;
                }
                NVector3 forward = N(barrel.forward), desired = N(Target(__instance.aimRay, hull) - barrel.position);
                // A share of the error each frame: the whole of it overshoots while the struts lag (bobbing), and
                // measured pitch plus error settles off by the struts' steady lag
                Plugin.PitchTarget = Ride.Current.Pitch + Pitch(forward, desired, N(hull.right)) * MathF.Min(1f, Time.deltaTime / PitchSmoothing);
                if (steering)
                {
                    // The player steered this frame: theirs wins, and ours starts again from 0
                    steering = false;
                    turning = false;
                    command = 0f;
                    return;
                }
                float yawRate = hull.GetComponent<Rigidbody>() is { } body ? Vector3.Dot(body.angularVelocity, hull.up) * Mathf.Rad2Deg : 0f;
                float yaw = Yaw(forward, desired, N(hull.up));
                bool wasTurning = turning;
                (command, turning) = Steer(yaw, yawRate, command, Time.deltaTime, turning);
                if (!turning)
                {
                    // Handing over from the tracks: start at the push that last kept the hull moving, not from 0,
                    // or it stops on friction and waits for the integral to build (a pause)
                    if (wasTurning)
                        layIntegral = MathF.CopySign(layHold, yaw);
                    layError = yaw;
                    aimed = Time.realtimeSinceStartup;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Hull aiming failed; switching it off: {e}");
                failed = true;
                Enabled = false;
                Stop();
            }
        }

        /// <summary>
        /// The laying drive: equal and opposite forces along the hull at each
        /// track's arms, a pure yaw couple, only while both tracks touch the ground.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(TrackBehaviour), nameof(TrackBehaviour.FixedUpdate))]
        private static void Step(TrackBehaviour __instance)
        {
            if (failed || !PlayerVehicle.Contains(__instance.Transform))
                return;
            try
            {
                bool contact = __instance.InContact && !__instance.broken;
                if (__instance.sideMultiplier > 0)
                    rightContact = contact;
                else
                    leftContact = contact;
                // Aim stale past 0.1 s: the player isn't in control, so nothing to lay
                if (!Enabled || float.IsNaN(layError) || Time.realtimeSinceStartup - aimed > 0.1f || !leftContact || !rightContact
                    || PlayerVehicle.Root is not { } hull || hull.GetComponent<Rigidbody>() is not { } body
                    || __instance.suspensionArmTransforms is not { Length: > 0 } arms)
                {
                    layIntegral = 0f;
                    layCapped = false;
                    return;
                }
                Vector3 at = Vector3.zero;
                foreach (Transform arm in arms)
                    at += arm.position;
                at /= arms.Length;
                float x = Vector3.Dot(at - body.worldCenterOfMass, hull.right);
                if (MathF.Abs(x) < 0.1f)
                    return;
                // Once a step, on the left track's call; both tracks give half the moment
                if (__instance.sideMultiplier < 0)
                {
                    Vector3 up = hull.up;
                    float accel;
                    float rate = Vector3.Dot(body.angularVelocity, up) * Mathf.Rad2Deg;
                    (accel, layIntegral) = Lay(layError, rate, layIntegral, Time.fixedDeltaTime, layRate, layCapped);
                    // Moving the way it pushes: about the push sliding takes
                    if (MathF.Abs(rate) > 0.5f && MathF.Sign(rate) == MathF.Sign(layIntegral))
                        layHold = MathF.Abs(layIntegral);
                    // A force f at x along forward turns the hull by -x·f about up: |f| per track for half the moment
                    float cap = layGrip * body.mass * Physics.gravity.magnitude / 2f;
                    layForce = accel * Ride.Inertia(body, up) / (2f * MathF.Abs(x));
                    layCapped = MathF.Abs(layForce) > cap;
                    layForce = Math.Clamp(layForce, -cap, cap);
                }
                body.AddForceAtPosition(hull.forward * (-MathF.Sign(x) * layForce), at, ForceMode.Force);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Hull laying drive failed; switching hull aiming off: {e}");
                failed = true;
                Enabled = false;
                Stop();
            }
        }

        /// <summary>Feeds the steer to the player's controls while they aren't steering.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(VehicleInputSystemInputs), nameof(VehicleInputSystemInputs.SteerValue), MethodType.Getter)]
        private static void SteerValue(ref float __result)
        {
            if (!Enabled || !Active)
                return;
            if (MathF.Abs(__result) >= Steering)
                steering = true;
            else
                __result = command;
        }

        // Cannon blueprint, its laying speed and force properties
        private static readonly ObjectCache<FloatProperty> rates = new(), forces = new();

        /// <summary>Draws a "Hull aiming" checkbox in the cannon panel; it edits the tag in the cannon's name.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
        private static void Draw(CannonEditor __instance, IGUILayout __0)
        {
            Cannon? cannon = __instance.Component;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null || cannon?.Blueprint is not { } blueprint)
                return;
            void Set((float, float)? settings)
            {
                blueprint.Name = WithSettings(blueprint.Name ?? "", settings);
                cannon.blueprintSlot?.MarkModified();
            }
            var settings = Settings(blueprint.Name);
            drawer.ToggleField(
                "Hull aiming",
                settings != null,
                (Action<bool>)(value =>
                {
                    Set(value ? (DefaultRate, DefaultForce) : null);
                    __instance.RequestRedraw();
                }),
                "The hull turns on its tracks and pitches on its suspension to aim this gun at your crosshair. Only your own vehicle; steering keys always win. Saved with the vehicle.");
            if (settings == null)
                return;
            drawer.Float(rates.Get(blueprint, () => new FloatProperty(
                "Laying speed",
                (Func<float>)(() => Settings(blueprint.Name)?.Rate ?? DefaultRate),
                (Action<float>)(value => Set((value, Settings(blueprint.Name)?.Force ?? DefaultForce))),
                "How fast (°/s) the laying drive turns the hull for the last few degrees, after the tracks.")
            {
                Min = 0.5f,
                Max = 30f,
                SoftMin = 0.5f,
                SoftMax = 10f,
                UseSoftLimits = true,
                Step = 0.5f
            }));
            drawer.Float(forces.Get(blueprint, () => new FloatProperty(
                "Laying force",
                (Func<float>)(() => Settings(blueprint.Name)?.Force ?? DefaultForce),
                (Action<float>)(value => Set((Settings(blueprint.Name)?.Rate ?? DefaultRate, value))),
                "The laying drive's strongest push per track, as a % of the weight on it. Too little and the hull sticks on its tracks' friction.")
            {
                Min = 5f,
                Max = 100f,
                Step = 5f
            }));
            foreach (TrackAssembly track in cannon.transform.root.GetComponentsInChildren<TrackAssembly>())
                if (Plugin.Settings(track.blueprintSlot?.Blueprint?.Name) != null)
                    return;
            drawer.InfoField("Elevation needs Adjustable suspension on the tracks.", 1);
        }
    }
}
