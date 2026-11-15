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
        internal static ConfigEntry<float> FinWeight = null!;
        internal static ConfigEntry<float> SaclosYear = null!, BeamYear = null!;
        internal static ConfigEntry<float> WireSpeed = null!;

        /// <summary>Fastest <paramref name="guidance"/> lets its round fly, m/s: a wire's payout limit, or none.</summary>
        internal static float MaxSpeed(Guidance guidance) => guidance.Beam ? float.PositiveInfinity : WireSpeed.Value;

        /// <summary>The speed round <paramref name="id"/>'s max turn is rated at: burnout, or the wire's limit if lower.</summary>
        internal static float Rated(CannonBlueprint gun, string id, Guidance guidance) =>
            MathF.Min(RocketPropelled.Plugin.Speeds(gun, id)?.Burnout ?? 0f, MaxSpeed(guidance));

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
            FinWeight = Config.Bind("Guidance", "Fin weight", 0.25f, new ConfigDescription(
                "Calibers of guidance section added per g of max turn: bigger fins and the actuators to move them weigh more. Dead weight, as the guidance unit.",
                new AcceptableValueRange<float>(0f, 2f), new Step(0.05f)));
            UnitLength.SettingChanged += (_, _) => Loadout.Invalidate();
            FinWeight.SettingChanged += (_, _) => Loadout.Invalidate();
            RocketPropelled.Plugin.Payload = Weight;
            RocketPropelled.Plugin.ExtraDrag = FinDragOf;
            LiftToDrag = Config.Bind("Guidance", "Lift to drag", 4f, new ConfigDescription(
                "Turning costs speed: every m/s² the fins pull, holding the round's weight too, slows it by 1/this m/s². Lower bleeds speed faster, so rounds without a motor sink sooner.",
                new AcceptableValueRange<float>(1f, 20f), new Step(0.5f)));
            FinDrag.SettingChanged += (_, _) => Loadout.Invalidate();
            Hud.AddPanel(Indicator.Draw);
            SaclosYear = Introduced.Bind(Config, "Guidance", "SACLOS (wire) guidance", 1966f, "SACLOS introduced");
            BeamYear = Introduced.Bind(Config, "Guidance", "beam-riding guidance", 1981f, "Beam riding introduced");
            WireSpeed = Config.Bind("Guidance", "Max wire speed", 300f, new ConfigDescription(
                "Fastest a wire-guided (SACLOS) round flies while its wire pays out, m/s: faster and the wire would snap, so the round is held to this until the wire runs out. Beam riders have no limit. TOW tops out near 280.",
                new AcceptableValueRange<float>(50f, 2000f), new SoftMax(600f), new Step(10f)));
            SettingsTab.AddSection("Rounds", "Guidance", SaclosYear, BeamYear, WireSpeed, FinDrag, LiftToDrag, UnitLength, FinWeight);
            Loadout.Modify(Modify);
            Loadout.AddFields(GuidanceEditor.Draw);
            Loadout.Launched += Launch;
            new Harmony("madi.guidedmunitions").PatchAll();
        }

        /// <summary>Round <paramref name="id"/>'s guidance in <paramref name="gun"/>; null without guidance or a motor to fly it.</summary>
        internal static Guidance? GuidanceOf(CannonBlueprint gun, string id) =>
            RocketPropelled.Plugin.HasMotor(gun, id) ? Loadout.Get<GuidedRound>(gun, id)?.Guidance : null;

        /// <summary>Round <paramref name="id"/>'s guidance unit and fins in <paramref name="gun"/>, kg; 0 without guidance.</summary>
        internal static float Weight(CannonBlueprint gun, string id)
        {
            Guidance? guidance = GuidanceOf(gun, id);
            if (guidance == null)
                return 0f;
            // A full-bore section: the unit's length, and more for each g the fins pull
            float calibers = UnitLength.Value + FinWeight.Value * guidance.MaxG;
            return MathF.PI / 4f * MathF.Pow(gun.Caliber / 1000f, 3f) * calibers * UnitDensity;
        }

        /// <summary>Round <paramref name="id"/>'s drag over its own from its fins, as a multiple; 1 without guidance.</summary>
        internal static float FinDragOf(CannonBlueprint gun, string id)
        {
            Guidance? guidance = GuidanceOf(gun, id);
            if (guidance == null)
                return 1f;
            return 1f + FinDrag.Value * guidance.MaxG;
        }

        /// <summary>A guided round's fins add drag.</summary>
        private static bool Modify(ProjectileDefinition game, Cannon cannon, string id, RoundShape shape, List<ProjectileFunctionDefinition> functions)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return false;
            Guidance? guidance = GuidanceOf(gun, id);
            if (guidance == null)
                return false;
            shape.Drag *= 1f + FinDrag.Value * guidance.MaxG;
            return true;
        }

        /// <summary>Guides a guided round: the player's by their crosshair now, the AI's once its gunner says what it fired at.</summary>
        private static void Launch(IntPtr register, int projectile, ProjectileDefinition definition, CannonBlueprint owner, string id, Cannon gun)
        {
            Guidance? guidance = GuidanceOf(owner, id);
            if (guidance == null)
                return;
            if (!loggedLifetime)
            {
                Logger.LogInfo($"Guided round: projectile lifetime {definition.Lifetime}, min speed {definition.MinSpeed}");
                loggedLifetime = true;
            }
            // Slow rounds flying 3 km need longer than a shell
            if (definition.Lifetime < MinLifetime)
                definition.Lifetime = MinLifetime;
            // Max turn is rated at burnout speed (or the wire's limit), as the Turn radius readout shows it
            float rated = Rated(owner, id, guidance);
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
            bool beam = guidance.Beam;
            float maxSpeed = MaxSpeed(guidance);
            var motion = new LineMotion();
            // The player's round (no AI label) shows on the HUD
            Indicator.Shot? shot = null;
            if (label == null)
            {
                string link = beam ? "beam" : "wire";
                shot = Indicator.Track(wire, link);
            }
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
                        return Ends(beam ? "out of the beam's range" : "wire ran out", age, position);
                    (Vector3 Origin, Vector3 Direction)? sighted = sight(position, velocity);
                    if (sighted == null)
                        return Ends("sight lost", age, position);
                    (Vector3 Origin, Vector3 Direction) line = sighted.Value;
                    bool onLine = beam ? Saclos.InBeam(position, line.Origin, line.Direction) : Saclos.InView(position, line.Origin, line.Direction);
                    if (!onLine)
                        return Ends(beam ? "left the beam" : "out of the sight's view", age, position);
                    if (Terrain.Cuts(line.Origin, position))
                        return Ends("terrain cut the line", age, position);
                    motion.Update(line.Origin, line.Direction, dt);
                    velocity = Saclos.Steer(position, velocity, line.Origin, line.Direction, accel, dt, rated, LiftToDrag.Value, omega,
                        motion.OriginVelocity, motion.TurnRate);
                    velocity = Saclos.Capped(velocity, maxSpeed);
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
    /// <remarks>
    /// The game has no "Terrain" layer, so this casts against every layer and
    /// counts only <c>TerrainCollider</c> hits; vehicles and props on the line don't cut it.
    /// </remarks>
    internal static class Terrain
    {
        // (layer, collider type) pairs already logged, so a map's blockers show once each in the log
        private static readonly HashSet<(int, string)> seen = new();

        internal static bool Cuts(Vector3 from, Vector3 to)
        {
            var start = new UnityVector3(from.X, from.Y, from.Z);
            var line = new UnityVector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            foreach (UnityPhysicsModule::UnityEngine.RaycastHit hit in Physics.RaycastAll(start, line.normalized, line.magnitude))
            {
                if (hit.collider == null)
                    continue;
                string type = hit.collider.GetIl2CppType().Name;
                if (seen.Add((hit.collider.gameObject.layer, type)))
                    Plugin.Logger.LogInfo($"Guidance line crossed a {type} on layer {hit.collider.gameObject.layer} ({LayerMask.LayerToName(hit.collider.gameObject.layer)})");
                if (type == "TerrainCollider")
                    return true;
            }
            return false;
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
            if (pending.Count == 0)
                return;
            if (e == null)
                return;
            if (!pending.Remove(e.ProjectileID, out (IntPtr Register, Guidance Guidance, float Rated, float Time) shot))
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
                if (vehicle == null)
                    continue;
                if (own != null)
                {
                    if (vehicle.Pointer == own.Pointer)
                        continue;
                    if (SameTeam(own, vehicle))
                        continue;
                }
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
        private static bool SameTeam(Vehicle a, Vehicle b)
        {
            VehicleBehaviour? x = a.behaviour;
            if (x == null)
                return false;
            VehicleBehaviour? y = b.behaviour;
            if (y == null)
                return false;
            return x.ID.TeamID == y.ID.TeamID;
        }

        /// <summary>Where a round aims on <paramref name="vehicle"/>: its centre of mass, else its origin.</summary>
        private static Vector3 Aim(Vehicle vehicle)
        {
            Rigidbody? body = vehicle.GetComponent<Rigidbody>();
            if (body != null)
                return N(body.worldCenterOfMass);
            return N(vehicle.transform.position);
        }

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
            UnityCoreModule::UnityEngine.Ray ray = __instance.aimRay;
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
        // Label, stored mode (null for none), first year (null for always)
        private static readonly (string Label, string? Mode, Func<float>? Year)[] options =
        {
            ("None", null, null),
            ("SACLOS", Guidance.Saclos, () => Plugin.SaclosYear.Value),
            ("Beam riding", Guidance.BeamRiding, () => Plugin.BeamYear.Value),
        };

        // Gun and round, its "not available until" row
        private static readonly Dictionary<(IntPtr, string), StringProperty> notes = new();

        private static float YearOf(string? mode) => options.FirstOrDefault(o => o.Mode == mode).Year?.Invoke() ?? 0f;

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            // Only rocket rounds are guided
            if (!RocketPropelled.Plugin.HasMotor(gun, id))
                return;
            string? mode = Loadout.Get<GuidedRound>(gun, id)?.Guidance?.Mode;
            // Modes too new for the vehicle are hidden, except the round's own
            List<(string Label, string? Mode, Func<float>? Year)> offered = options.Where(o => o.Mode == mode || Introduced.Allowed(o.Year?.Invoke() ?? 0f)).ToList();
            EditorGui.Dropdown(drawer, "Guidance", offered.Select(o => o.Label).ToList(), offered.FindIndex(o => o.Mode == mode), i =>
            {
                string? picked = offered[i].Mode;
                Loadout.Edit<GuidedRound>(gun, id, r =>
                {
                    if (picked == null)
                        r.Guidance = null;
                    else
                        (r.Guidance ??= new Guidance()).Mode = picked;
                });
                changed();
                Loadout.Redraw();
            }, "SACLOS: the round steers onto its gunner's sight line, so it hits what the sight stays on, burning or coasting, until its wire runs out or terrain cuts the line to it. "
                + $"Downsides: the wire holds it to {Plugin.WireSpeed.Value:0} m/s (Max wire speed), so a hot motor is wasted and the round takes longer to arrive; its range ends with the wire. "
                + $"Beam riding: the same, steering by a beam along the sight line instead of a wire, with no speed limit. Downside: once {Saclos.GatherDistance:0} m out it must stay within {Saclos.BeamRadius:0} m of the beam's centre, so a fast swing of the sight loses it. "
                + "The AI's gunners guide theirs onto their target. Kinds newer than the vehicle's year are hidden (MadiLib's Historical availability).");
            if (mode == null)
                return;
            if (!Introduced.Allowed(YearOf(mode)))
            {
                if (!notes.TryGetValue((gun.Pointer, id), out StringProperty? note))
                {
                    note = EditorGui.Readout("Too new",
                        () => Introduced.Note(YearOf(Loadout.Get<GuidedRound>(gun, id)?.Guidance?.Mode)),
                        "This guidance is newer than the vehicle's year. It still works; pick another or a later year to clear this.");
                    notes[(gun.Pointer, id)] = note;
                }
                drawer.Readonly(note);
            }
            // Per mode, so the range field's label follows it
            RoundFields.Draw(drawer, RoundFields.For(gun, id, "guidance." + mode, () => Make(gun, id, changed, mode == Guidance.BeamRiding)));
        }

        // Per gun and round: the inputs "Hits at" was last flown for, and its result
        private static readonly Dictionary<(IntPtr, string), ((float, float, float, float, float, float, int, float) For, (float Min, float Max) Result)> hitRanges = new();

        private static (FloatProperty[], StringProperty[]) Make(CannonBlueprint gun, string id, Action changed, bool beam)
        {
            Guidance G() => Loadout.Get<GuidedRound>(gun, id)?.Guidance ?? new Guidance();
            StringProperty[] readouts =
            {
                EditorGui.Readout("Turn radius", () =>
                {
                    float v = Plugin.Rated(gun, id, G());
                    return $"{v * v / (G().MaxG * Saclos.Gravity):0} m";
                }, "Tightest turn at burnout velocity (a wire-guided round's held to Max wire speed), without drag or gravity."),
                EditorGui.Readout("Hits at", () =>
                {
                    Guidance g = G();
                    (float Launch, float Burnout)? speeds = RocketPropelled.Plugin.Speeds(gun, id);
                    Func<float, float>? thrust = RocketPropelled.Plugin.Thrust(gun, id);
                    // Flown again only when an input changes: the thrust curve counts as its samples at the sim's steps
                    var hash = new HashCode();
                    float maxSpeed = Plugin.MaxSpeed(g);
                    hash.Add(maxSpeed);
                    // Drag counts as its samples too: Body drag (rocketPropelled) and the fins'
                    Func<float, float>? decel = RocketPropelled.Plugin.Decel(gun, id);
                    if (decel != null)
                        for (float v = 50f; v <= 1000f; v += 50f)
                            hash.Add(decel(v));
                    if (thrust != null)
                        for (float t = 0f; t < 30f; t += 0.02f)
                            hash.Add(thrust(t));
                    (float, float, float, float, float, float, int, float) key =
                        (speeds?.Launch ?? 0f, g.MaxG, g.WireLength, Plugin.Rated(gun, id, g), Plugin.LiftToDrag.Value, g.Response, hash.ToHashCode(), Plugin.FinDrag.Value);
                    if (!hitRanges.TryGetValue((gun.Pointer, id), out ((float, float, float, float, float, float, int, float) For, (float Min, float Max) Result) cached) || cached.For != key)
                    {
                        (float Min, float Max) result = Saclos.HitRanges(key.Item1, g.MaxG * Saclos.Gravity, g.WireLength, key.Item4,
                            key.Item5, g.Response, thrust, decel: decel, maxSpeed: maxSpeed);
                        cached = (key, result);
                        hitRanges[(gun.Pointer, id)] = cached;
                    }
                    (float min, float max) = cached.Result;
                    return float.IsInfinity(min) ? "misses" : $"{min:0}-{max:0} m";
                }, "Target ranges it passes within 0.5 m of, fired from 2 m below the sight at a still target under the crosshair, out to the wire or beam range. Its motor, guidance unit and fin weight, ignition delay, drag (body and fins) and turn costs count; a full-bore shell is the warhead."),
                EditorGui.Readout("Guidance weight", () => $"{Plugin.Weight(gun, id):0.00} kg",
                    "Guidance unit and fins, dead weight launched with the motor. Grows with Max turn."),
                EditorGui.Readout("Fin drag", () => $"+{Plugin.FinDrag.Value * G().MaxG * 100f:0}%", "Extra drag from the fins, over the round's own."),
                beam
                    ? EditorGui.Readout("Beam", () => $"stay within {Saclos.BeamRadius:0} m",
                        $"Once {Saclos.GatherDistance:0} m out, the round is lost if it strays more than {Saclos.BeamRadius:0} m from the beam's centre: swing the sight slowly enough for it to follow.")
                    : EditorGui.Readout("Top speed", () =>
                    {
                        float burnout = RocketPropelled.Plugin.Speeds(gun, id)?.Burnout ?? 0f, wire = Plugin.WireSpeed.Value;
                        // Short: the readout column wraps past about 16 characters
                        return burnout > wire ? $"{wire:0} m/s capped" : $"{burnout:0} m/s";
                    }, "Fastest the round flies guided. A wire-guided round is held to Max wire speed (Guidance settings) until the wire runs out, or the wire would snap; \"capped\" means the motor could go faster, and that thrust is wasted. Beam riding has no limit."),
            };
            FloatProperty Field(string name, Func<Guidance, float> get, Action<Guidance, float> set, string tooltip, float min, float softMax, float max, float step) =>
                RoundFields.Field<GuidedRound>(gun, id, name, () => get(G()), (r, value) => set(r.Guidance ??= new Guidance(), value), tooltip, changed, readouts, min, step, max, softMax);
            FloatProperty[] fields =
            {
                Field("Max turn", g => g.MaxG, (g, v) => g.MaxG = v,
                    "Hardest turn the fins pull, in g, holding up the round's weight too. More turns tighter and holds the line better, but the fins add drag.", 1f, 20f, 50f, 0.5f),
                Field(beam ? "Beam range" : "Wire length", g => g.WireLength, (g, v) => g.WireLength = v,
                    beam ? "Metres from launch the beam reaches; the round flies on unguided past it."
                        : "Metres the round flies from launch before its command link runs out; it flies on unguided after.", 500f, 5000f, 10000f, 100f),
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

    /// <summary>Command guidance: the round steers onto the gunner's sight line, by wire (SACLOS) or riding a beam.</summary>
    internal sealed class Guidance
    {
        internal const string Saclos = "saclos", BeamRiding = "beam";

        /// <summary>How the round is guided: <see cref="Saclos"/> (also older files, which have none) or <see cref="BeamRiding"/>.</summary>
        public string Mode { get; set; } = Saclos;

        /// <summary>Whether it rides a beam rather than following a wire.</summary>
        internal bool Beam => Mode == BeamRiding;

        /// <summary>Hardest turn, g.</summary>
        public float MaxG { get; set; } = 10f;

        /// <summary>Metres from launch before the command link runs out: the wire's length, or the beam's range.</summary>
        public float WireLength { get; set; } = 3000f;

        /// <summary>How fast it settles onto the sight line, rad/s.</summary>
        public float Response { get; set; } = GuidedMunitions.Saclos.DefaultOmega;
    }
}
