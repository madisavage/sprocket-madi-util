extern alias UnityCoreModule;
extern alias UnityPhysicsModule;

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using RocketPropelled;
using Rounds;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using LayerMask = UnityCoreModule::UnityEngine.LayerMask;
using Physics = UnityPhysicsModule::UnityEngine.Physics;
using Rigidbody = UnityPhysicsModule::UnityEngine.Rigidbody;
using UnityObject = UnityCoreModule::UnityEngine.Object;
using Time = UnityCoreModule::UnityEngine.Time;
using UnityVector3 = UnityCoreModule::UnityEngine.Vector3;
using Vector3 = System.Numerics.Vector3;

namespace GuidedMunitions
{
    /// <summary>
    /// BepInEx entry point. A round with a rocket motor (rocketPropelled) can
    /// also carry SACLOS guidance: it steers onto its gunner's line of sight
    /// (the player's crosshair, or the AI gunner's target), as far as its wire
    /// reaches and while terrain doesn't cut the line to it, burning or coasting.
    /// </summary>
    [BepInPlugin("madi.guidedmunitions", "guidedMunitions", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rocketpropelled", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static ConfigEntry<float> FinDrag = null!;
        internal static ConfigEntry<float> LiftToDrag = null!;
        internal static ConfigEntry<float> UnitLength = null!;

        // shortcut: a guidance unit is a full-bore section of this density, kg/m³; make it a config if it needs tuning
        private const float UnitDensity = 1500f;

        // shortcut: assumes the definition's Lifetime is in seconds; check the logged value in game
        private const float MinLifetime = 60f;
        private static bool loggedLifetime;

        public override void Load()
        {
            Logger = Log;
            FinDrag = Config.Bind("Guidance", "Fin drag", 0.1f, new ConfigDescription(
                "Extra drag per g of max turn: bigger fins turn harder but slow the round. 0.1 is double the drag at 10 g.",
                new AcceptableValueRange<float>(0f, 0.5f), new SoftMax(0.3f), new Step(0.01f)));
            UnitLength = Config.Bind("Guidance", "Unit length", 1f, new ConfigDescription(
                "Length in calibers of a guided round's guidance unit, a full-bore section of 1500 kg/m³. The charge pushes it and it's dead weight all flight, so guided rounds leave slower and speed up less.",
                new AcceptableValueRange<float>(0f, 4f), new Step(0.25f)));
            UnitLength.SettingChanged += (_, _) => Loadout.Invalidate();
            RocketPropelled.Plugin.Payload = (gun, id) => GuidanceOf(gun, id) == null ? 0f
                : MathF.PI / 4f * MathF.Pow(gun.Caliber / 1000f, 3f) * UnitLength.Value * UnitDensity;
            LiftToDrag = Config.Bind("Guidance", "Lift to drag", 4f, new ConfigDescription(
                "Turning costs speed: every m/s² the fins pull, holding the round's weight too, slows it by 1/this m/s². Lower bleeds speed faster, so rounds without a motor sink sooner.",
                new AcceptableValueRange<float>(1f, 20f), new Step(0.5f)));
            FinDrag.SettingChanged += (_, _) => Loadout.Invalidate();
            Hud.AddPanel(Indicator.Draw);
            SettingsTab.AddSection("Rounds", "Guidance", FinDrag, LiftToDrag, UnitLength);
            Loadout.Modify(Modify);
            Loadout.AddFields(GuidanceEditor.Draw);
            Loadout.Launched += Launch;
            new Harmony("madi.guidedmunitions").PatchAll();
        }

        /// <summary>Round <paramref name="id"/>'s guidance in <paramref name="gun"/>; null without guidance or a motor to fly it.</summary>
        internal static Guidance? GuidanceOf(CannonBlueprint gun, string id) =>
            RocketPropelled.Plugin.HasMotor(gun, id) ? Loadout.Get<GuidedRound>(gun, id)?.Guidance : null;

        /// <summary>A guided round's fins add drag.</summary>
        private static bool Modify(ProjectileDefinition game, Cannon cannon, string id, RoundShape shape, List<ProjectileFunctionDefinition> functions)
        {
            if (cannon.Blueprint is not { } gun || GuidanceOf(gun, id) is not { } guidance)
                return false;
            shape.Drag *= 1f + FinDrag.Value * guidance.MaxG;
            return true;
        }

        /// <summary>Guides a guided round: the player's by their crosshair now, the AI's once its gunner says what it fired at.</summary>
        private static void Launch(IntPtr register, int projectile, ProjectileDefinition definition, CannonBlueprint owner, string id, Cannon gun)
        {
            if (GuidanceOf(owner, id) is not { } guidance)
                return;
            if (!loggedLifetime)
            {
                Logger.LogInfo($"Guided round: projectile lifetime {definition.Lifetime}, min speed {definition.MinSpeed}");
                loggedLifetime = true;
            }
            // Slow rounds flying 3 km need longer than a shell
            if (definition.Lifetime < MinLifetime)
                definition.Lifetime = MinLifetime;
            // Max turn is rated at burnout speed, as the Turn radius readout shows it
            float rated = RocketPropelled.Plugin.Speeds(owner, id)?.Burnout ?? 0f;
            if (PlayerVehicle.Contains(gun))
                Guide(register, projectile, guidance, rated, (_, _) => Sight.Line, null);
            else
                AiSight.Expect(register, projectile, guidance, rated);
        }

        private static bool stepFailed;

        /// <summary>
        /// Steers projectile <paramref name="projectile"/> by <paramref name="guidance"/> onto <paramref name="sight"/>'s line
        /// (origin, unit direction), given the round's position and velocity, each step, until the line is lost (null), terrain cuts it
        /// from the round, or the wire runs out; the sight tracks a beacon on the round, so it steers burning or not.
        /// Max turn is rated at <paramref name="rated"/> m/s. With <b>Log AI</b>, logs why it ends under
        /// <paramref name="label"/>; null logs nothing.
        /// </summary>
        internal static void Guide(IntPtr register, int projectile, Guidance guidance, float rated, Func<Vector3, Vector3, (Vector3 Origin, Vector3 Direction)?> sight, string? label)
        {
            Vector3? start = null;
            float accel = guidance.MaxG * Saclos.Gravity, wire = guidance.WireLength, omega = guidance.Response;
            var motion = new LineMotion();
            // The player's round (no AI label) shows on the HUD
            Indicator.Shot? shot = label == null ? Indicator.Track(wire) : null;
            bool Ends(string why, float age, Vector3 position)
            {
                if (shot != null)
                {
                    shot.Lost = why;
                    shot.LostAt = Time.time;
                }
                if (label != null && Loadout.LogAi)
                    Logger.LogInfo($"{label}: guidance ends after {age:0.0} s, {Vector3.Distance(position, start!.Value):0} m: {why}");
                return false;
            }
            Flight.Follow(register, projectile, (float age, float dt, Vector3 position, ref Vector3 velocity) =>
            {
                try
                {
                    start ??= position;
                    if (shot != null)
                    {
                        shot.Position = position;
                        shot.Seen = Time.time;
                        shot.Distance = Vector3.Distance(position, start.Value);
                    }
                    if (Vector3.Distance(position, start.Value) > wire)
                        return Ends("wire ran out", age, position);
                    if (sight(position, velocity) is not { } line)
                        return Ends("sight lost", age, position);
                    if (!Saclos.InView(position, line.Origin, line.Direction))
                        return Ends("out of the sight's view", age, position);
                    if (Terrain.Cuts(line.Origin, position))
                        return Ends("terrain cut the line", age, position);
                    motion.Update(line.Origin, line.Direction, dt);
                    velocity = Saclos.Steer(position, velocity, line.Origin, line.Direction, accel, dt, rated, LiftToDrag.Value, omega,
                        motion.OriginVelocity, motion.TurnRate);
                    return true;
                }
                catch (Exception e)
                {
                    // A dead gunner or target can throw; the round flies on, and other rockets keep flying
                    if (!stepFailed)
                        Logger.LogError($"Guidance step failed, round flies on unguided: {e}");
                    stepFailed = true;
                    return false;
                }
            });
        }
    }

    /// <summary>Whether terrain stands between a sight and its round, which cuts the guidance.</summary>
    internal static class Terrain
    {
        // The terrain layer's mask; 0 if the game has no "Terrain" layer, which turns the cut off
        private static int? mask;

        internal static bool Cuts(Vector3 from, Vector3 to)
        {
            if (mask == null)
            {
                int layer = LayerMask.NameToLayer("Terrain");
                mask = layer < 0 ? 0 : 1 << layer;
                if (layer < 0)
                    Plugin.Logger.LogWarning("No \"Terrain\" layer, so terrain doesn't cut guidance. Layers: "
                        + string.Join(", ", Enumerable.Range(0, 32).Select(i => $"{i} {LayerMask.LayerToName(i)}").Where(n => n.Length > 3)));
            }
            return mask != 0 && Physics.Linecast(new UnityVector3(from.X, from.Y, from.Z), new UnityVector3(to.X, to.Y, to.Z), mask.Value);
        }
    }

    /// <summary>
    /// The AI gunner's line of sight: from its gun to the vehicle it fired
    /// at, taken as the vehicle nearest the round's launch direction. A guided
    /// round the AI fires waits here until the gunner's fire event names its projectile.
    /// </summary>
    /// <remarks>
    /// Not the AI's own target tracking: its <c>ShortTargetInfo</c> lives in
    /// native arrays and works out its id and position in native methods,
    /// which threw when read through the interop (2026-11-10).
    /// </remarks>
    [HarmonyPatch(typeof(GunnerAI), nameof(GunnerAI.OnGunnerFired))]
    internal static class AiSight
    {
        // Widest angle off the launch direction a vehicle can be to be taken as the target, degrees; covers the AI's lead and superelevation
        private const float MaxOffAngle = 15f;

        // Projectile id, its register, guidance, rated speed and when it launched
        private static readonly Dictionary<int, (IntPtr Register, Guidance Guidance, float Rated, float Time)> pending = new();

        internal static void Expect(IntPtr register, int projectile, Guidance guidance, float rated)
        {
            // A round no gunner claimed, such as one the AI didn't fire, is dropped
            foreach (int old in pending.Where(p => Time.time - p.Value.Time > 1f).Select(p => p.Key).ToList())
                pending.Remove(old);
            pending[projectile] = (register, guidance, rated, Time.time);
        }

        private static void Postfix(GunnerAI __instance, GunnerFireEventArgs e)
        {
            if (pending.Count == 0 || e == null || !pending.Remove(e.ProjectileID, out var shot))
                return;
            IGunnerControl gunner = __instance.Gunner;
            Vehicle? own = gunner?.WeaponGameObject?.GetComponentInParent<Vehicle>();
            string label = $"AI {own?.name ?? "vehicle"} guided round {e.ProjectileID}";
            if (gunner == null)
                return;
            Vehicle? target = null;
            bool picked = false;
            Plugin.Guide(shot.Register, e.ProjectileID, shot.Guidance, shot.Rated, (position, velocity) =>
            {
                if (!picked)
                {
                    picked = true;
                    target = Nearest(own, position, velocity, out float angle);
                    if (target == null)
                    {
                        Log($"{label}: no vehicle within {MaxOffAngle}° of its launch direction, flies unguided");
                        return null;
                    }
                    Log($"{label}: steering at {target.name}, {Vector3.Distance(position, Aim(target)):0} m away, {angle:0.0}° off its launch direction");
                }
                if (target == null)
                {
                    Log($"{label}: target gone");
                    return null;
                }
                if (!gunner.Functioning)
                {
                    Log($"{label}: gunner out of action");
                    return null;
                }
                Vector3 origin = N(gunner.FirePosition), direction = Aim(target) - origin;
                return direction.LengthSquared() < 1f ? null : (origin, Vector3.Normalize(direction));
            }, label);
        }

        /// <summary>The vehicle other than <paramref name="own"/> and its team nearest the line from <paramref name="position"/> along <paramref name="velocity"/> by angle, within <see cref="MaxOffAngle"/>; null for none.</summary>
        private static Vehicle? Nearest(Vehicle? own, Vector3 position, Vector3 velocity, out float angle)
        {
            Vehicle? best = null;
            angle = MaxOffAngle;
            Vector3 forward = Vector3.Normalize(velocity);
            foreach (Vehicle vehicle in UnityObject.FindObjectsOfType<Vehicle>())
            {
                if (vehicle == null || own != null && (vehicle.Pointer == own.Pointer || SameTeam(own, vehicle)))
                    continue;
                Vector3 to = Aim(vehicle) - position;
                float off = MathF.Acos(Math.Clamp(Vector3.Dot(forward, Vector3.Normalize(to)), -1f, 1f)) * 180f / MathF.PI;
                if (off < angle)
                {
                    angle = off;
                    best = vehicle;
                }
            }
            return best;
        }

        /// <summary>Whether both vehicles are spawned on the same team; false while either has no behaviour.</summary>
        private static bool SameTeam(Vehicle a, Vehicle b) =>
            a.behaviour is { } x && x != null && b.behaviour is { } y && y != null && x.ID.TeamID == y.ID.TeamID;

        /// <summary>Where a round aims on <paramref name="vehicle"/>: its centre of mass, else its origin.</summary>
        private static Vector3 Aim(Vehicle vehicle) =>
            N(vehicle.GetComponent<Rigidbody>() is { } body && body != null ? body.worldCenterOfMass : vehicle.transform.position);

        private static Vector3 N(UnityVector3 v) => new(v.x, v.y, v.z);

        private static void Log(string message)
        {
            if (Loadout.LogAi)
                Plugin.Logger.LogInfo(message);
        }
    }

    /// <summary>The gunner's line of sight, from the player's aim each frame of control.</summary>
    [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.UpdateControl))]
    internal static class Sight
    {
        // A sight line older than this, s, is lost: the player left the vehicle or it died
        private const float Stale = 0.5f;
        private static (Vector3 Origin, Vector3 Direction) line;
        private static float seen = float.NegativeInfinity;

        /// <summary>The player's sight line, origin and unit direction; null when lost.</summary>
        internal static (Vector3 Origin, Vector3 Direction)? Line =>
            Time.time - seen <= Stale && PlayerVehicle.Root != null ? line : null;

        private static void Postfix(VehicleController __instance)
        {
            seen = Time.time;
            // The game's "lock turret": the sight looks around, the line holds
            if (__instance.GunnerAimLocked)
                return;
            var ray = __instance.aimRay;
            var direction = new Vector3(ray.direction.x, ray.direction.y, ray.direction.z);
            line = (new Vector3(ray.origin.x, ray.origin.y, ray.origin.z), Vector3.Normalize(direction));
        }
    }

    /// <summary>
    /// Draws a <b>Guidance</b> picker under a rocket round's motor in the
    /// cannon panel and, with guidance, its max turn and readouts.
    /// </summary>
    internal static class GuidanceEditor
    {
        private static readonly string[] options = { "None", "SACLOS" };

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            if (cannon.Blueprint is not { } gun || !RocketPropelled.Plugin.HasMotor(gun, id))
                return;
            bool has = Loadout.Get<GuidedRound>(gun, id)?.Guidance != null;
            EditorGui.Dropdown(drawer, "Guidance", options, has ? 1 : 0, i =>
            {
                Loadout.Edit<GuidedRound>(gun, id, r => r.Guidance = i == 0 ? null : r.Guidance ?? new Guidance());
                changed();
                Loadout.Redraw();
            }, "SACLOS: the round steers onto its gunner's sight line, so it hits what the sight stays on, burning or coasting, until its wire runs out or terrain cuts the line to it. The AI's gunners guide theirs onto their target.");
            if (has)
                RoundFields.Draw(drawer, RoundFields.For(gun, id, "guidance", () => Make(gun, id, changed)));
        }

        private static (FloatProperty[], StringProperty[]) Make(CannonBlueprint gun, string id, Action changed)
        {
            Guidance G() => Loadout.Get<GuidedRound>(gun, id)?.Guidance ?? new Guidance();
            StringProperty[] readouts =
            {
                EditorGui.Readout("Turn radius", () =>
                {
                    float v = RocketPropelled.Plugin.Speeds(gun, id)?.Burnout ?? 0f;
                    return $"{v * v / (G().MaxG * Saclos.Gravity):0} m";
                }, "Tightest turn at burnout velocity, without drag or gravity."),
                EditorGui.Readout("Hits at", () =>
                {
                    Guidance g = G();
                    var speeds = RocketPropelled.Plugin.Speeds(gun, id);
                    var (min, max) = Saclos.HitRanges(speeds?.Launch ?? 0f, g.MaxG * Saclos.Gravity, g.WireLength, speeds?.Burnout ?? 0f,
                        Plugin.LiftToDrag.Value, g.Response, RocketPropelled.Plugin.Thrust(gun, id));
                    return float.IsInfinity(min) ? "misses" : $"{min:0}-{max:0} m";
                }, "Target ranges it passes within 0.5 m of, fired from 2 m below the sight at a still target under the crosshair, out to the wire length. Its motor, guidance unit, ignition delay and turn costs count; drag doesn't, so it's hopeful for long coasts, and a full-bore shell is the warhead."),
                EditorGui.Readout("Fin drag", () => $"+{Plugin.FinDrag.Value * G().MaxG * 100f:0}%", "Extra drag from the fins, over the round's own."),
            };
            FloatProperty Field(string name, Func<Guidance, float> get, Action<Guidance, float> set, string tooltip, float min, float softMax, float max, float step) =>
                RoundFields.Field<GuidedRound>(gun, id, name, () => get(G()), (r, value) => set(r.Guidance ??= new Guidance(), value), tooltip, changed, readouts, min, step, max, softMax);
            FloatProperty[] fields =
            {
                Field("Max turn", g => g.MaxG, (g, v) => g.MaxG = v,
                    "Hardest turn the fins pull, in g, holding up the round's weight too. More turns tighter and holds the line better, but the fins add drag.", 1f, 20f, 50f, 0.5f),
                Field("Wire length", g => g.WireLength, (g, v) => g.WireLength = v,
                    "Metres the round flies from launch before its command link runs out; it flies on unguided after.", 500f, 5000f, 10000f, 100f),
                Field("Response", g => g.Response, (g, v) => g.Response = v,
                    "How fast guidance pulls the round onto the sight line, rad/s: it settles in about 1/this s. Lower lags more, so it overshoots turns and needs more range to settle; higher snaps on but pulls harder.", 0.5f, 5f, 10f, 0.1f),
            };
            return (fields, readouts);
        }
    }

    /// <summary>The fields guidance adds to a round of any type, as stored in the vehicle file through <see cref="Loadout"/>.</summary>
    internal sealed class GuidedRound
    {
        /// <summary>The round's guidance; null for none.</summary>
        public Guidance? Guidance { get; set; }
    }

    /// <summary>SACLOS guidance: the round steers onto the gunner's sight line.</summary>
    internal sealed class Guidance
    {
        /// <summary>Hardest turn, g.</summary>
        public float MaxG { get; set; } = 10f;

        /// <summary>Metres from launch before the command link runs out.</summary>
        public float WireLength { get; set; } = 3000f;

        /// <summary>How fast it settles onto the sight line, rad/s.</summary>
        public float Response { get; set; } = Saclos.DefaultOmega;
    }
}
