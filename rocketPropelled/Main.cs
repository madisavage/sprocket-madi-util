extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;
using Time = UnityCoreModule::UnityEngine.Time;

namespace RocketPropelled
{
    /// <summary>
    /// BepInEx entry point. Any round can carry a rocket motor: the gun's
    /// charge pushes warhead and motor together, so it leaves slower than the
    /// warhead alone, then speeds up under the motor. The round's own type still decides its warhead, so
    /// its hit is that type's at the speed it arrives.
    /// </summary>
    [BepInPlugin("madi.rocketpropelled", "rocketPropelled", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", ">=" + ModInfo.Version)]
    [BepInDependency("sprocket.dynamicsight", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static ConfigEntry<float> Exhaust = null!;
        internal static ConfigEntry<float> PropellantDensity = null!;
        internal static ConfigEntry<float> CaseMass = null!;
        internal static ConfigEntry<bool> LogFlights = null!;

        /// <summary>Cannon blueprint and round id, its launch velocity in m/s, as last worked out for its projectile.</summary>
        internal static readonly Dictionary<(IntPtr, string), float> LaunchSpeeds = new();

        public override void Load()
        {
            Logger = Log;
            Exhaust = Config.Bind("Rocket", "Exhaust velocity", 2000f, new ConfigDescription(
                "Impulse per kg of propellant, N·s/kg (m/s). Early double-base powder is about 2000; better propellant pushes the same motor faster.",
                new AcceptableValueRange<float>(500f, 4000f), new Step(50f)));
            PropellantDensity = Config.Bind("Rocket", "Propellant density", 1300f, new ConfigDescription(
                "Propellant mass per volume of motor section, kg/m³, counting the gaps a grain leaves.",
                new AcceptableValueRange<float>(100f, 2500f), new Step(50f)));
            CaseMass = Config.Bind("Rocket", "Case mass", 0.5f, new ConfigDescription(
                "Motor case and nozzle mass over its propellant's. Dead weight: launched, and still carried after burnout.",
                new AcceptableValueRange<float>(0f, 3f), new Step(0.05f)));
            LogFlights = Config.Bind("Debug", "Log flights", false,
                "Logs each rocket's speed at launch and burnout, and Dynamic Gun Sight's prediction of them, to compare the two.");
            foreach (var entry in new[] { Exhaust, PropellantDensity, CaseMass })
                entry.SettingChanged += (_, _) => Loadout.Invalidate();
            SettingsTab.AddSection("Rounds", "Rocket", Exhaust, PropellantDensity, CaseMass);
            Loadout.Modify(Modify);
            Loadout.AddFields(MotorEditor.Draw);
            Loadout.Launched += Flight.Launch;
            var harmony = new Harmony("madi.rocketpropelled");
            harmony.PatchAll();
            if (Type.GetType("SprocketDynamicSight.Game.AmmoCatalog, SprocketDynamicSight") != null)
                DynamicSight.Patch(harmony);
        }

        /// <summary>The motor of round <paramref name="id"/> in <paramref name="gun"/>, built for the gun; null for none.</summary>
        internal static Burn? BurnOf(CannonBlueprint? gun, string id) =>
            gun != null && Loadout.Get<RocketRound>(gun, id)?.Motor is { } motor
                ? motor.Build(gun.Caliber, PropellantDensity.Value, CaseMass.Value, Exhaust.Value) : null;

        /// <summary>
        /// A round with a motor: the charge pushes the motor too, so it leaves
        /// slower, and the AI leads it by its burnout velocity.
        /// </summary>
        private static bool Modify(ProjectileDefinition game, Cannon cannon, string id, RoundShape shape, List<ProjectileFunctionDefinition> functions)
        {
            if (cannon.Blueprint is not { } gun || BurnOf(gun, id) is not { } burn)
                return false;
            // Launch length is mass as a full-bore projectile's length
            shape.LaunchLength += game.Length * burn.Mass / game.Mass;
            float launch = gun.MuzzleVelocity * Loadout.VelocityRatio(gun, shape.LaunchLength / game.Length);
            LaunchSpeeds[(gun.Pointer, id)] = launch;
            // shortcut: AI leads by burnout speed, no drag, so it overleads short shots; a drop table per round would fix it
            shape.AimVelocity = launch + burn.DeltaV(shape.Mass + burn.Case);
            return true;
        }
    }

    /// <summary>
    /// Pushes each rocket in flight, after the game moves its projectiles:
    /// a Harmony postfix on <c>ProjectileRegister.FixedUpdate</c>, which adds
    /// the motor's thrust to the projectile's velocity along its path. Drag
    /// and gravity stay the game's. After, not before: a push before a new
    /// projectile's first move came out a step short, as if that move set
    /// its velocity again.
    /// </summary>
    /// <remarks>
    /// Projectiles are structs in the register's <c>pool</c> array, found by
    /// <c>activeIdMap</c> (projectile id to pool index) and read at their
    /// native offsets, as rounds' detours do.
    /// </remarks>
    [HarmonyPatch(typeof(ProjectileRegister), nameof(ProjectileRegister.FixedUpdate))]
    internal static class Flight
    {
        private sealed record Rocket(IntPtr Register, int Id, float Launched, Burn Motor, float Dry)
        {
            // For Log flights: the launch point, and the next of the motor's checkpoints to log
            internal float[]? Start;
            internal int Next;

            /// <summary>Seconds after launch it stops being followed.</summary>
            internal float Until => Plugin.LogFlights.Value ? Math.Max(Motor.BurnOut, Motor.Checkpoints[^1]) : Motor.BurnOut;
        }

        private static readonly List<Rocket> burns = new();
        private static int size, idOffset, velocityOffset, positionOffset;

        /// <summary>Logs the rocket's path at its motor's checkpoints, as the sight's prediction is logged.</summary>
        private static unsafe void LogFlight(Rocket rocket, float* p, float speed, float age)
        {
            if (rocket.Start == null)
            {
                rocket.Start = new[] { p[0], p[1], p[2] };
                Plugin.Logger.LogInfo($"Rocket {rocket.Id} flight: {speed:0} m/s at launch ({age:0.000} s), step {Time.fixedDeltaTime:0.0000} s");
            }
            float[] checkpoints = rocket.Motor.Checkpoints;
            if (rocket.Next >= checkpoints.Length || age < checkpoints[rocket.Next])
                return;
            while (rocket.Next < checkpoints.Length && age >= checkpoints[rocket.Next])
                rocket.Next++;
            float x = p[0] - rocket.Start[0], z = p[2] - rocket.Start[2];
            Plugin.Logger.LogInfo($"Rocket {rocket.Id} flight at {age:0.000} s: range {MathF.Sqrt(x * x + z * z):0.0} m, height {p[1] - rocket.Start[1]:0.00} m, {speed:0} m/s");
        }

        internal static void Launch(IntPtr register, int projectile, ProjectileDefinition definition, CannonBlueprint owner, string id)
        {
            // The definition's mass is the warhead's, which its hits go by; the case flies too
            if (Plugin.BurnOf(owner, id) is { Burns: true } motor)
                burns.Add(new Rocket(register, projectile, Time.time, motor, definition.Mass + motor.Case));
        }

        private static unsafe void Postfix(ProjectileRegister __instance)
        {
            if (burns.Count == 0)
                return;
            if (size == 0)
            {
                IntPtr klass = Il2CppClassPointerStore<ProjectileInstance>.NativeClassPtr;
                // Field offsets count the object header, which a struct in an array doesn't have
                int Offset(string name) => (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(klass, name)) - 2 * IntPtr.Size;
                idOffset = Offset("ID");
                velocityOffset = Offset("velocity");
                positionOffset = Offset("position");
                uint align = 0;
                size = IL2CPP.il2cpp_class_value_size(klass, ref align);
            }
            var pool = __instance.pool;
            var active = __instance.activeIdMap;
            if (pool == null || active == null)
                return;
            float now = Time.time, dt = Time.fixedDeltaTime;
            for (int i = burns.Count - 1; i >= 0; i--)
            {
                Rocket burn = burns[i];
                float age = now - burn.Launched;
                if (burn.Register != __instance.Pointer)
                {
                    if (age >= burn.Until)
                        burns.RemoveAt(i);
                    continue;
                }
                bool live = active.TryGetValue(burn.Id, out int index) && index >= 0 && index < pool.Length;
                // An array's elements follow its header, bounds and length
                byte* projectile = (byte*)pool.Pointer + 4 * IntPtr.Size + (long)index * size;
                if (!live || *(int*)(projectile + idOffset) != burn.Id)
                {
                    // Hit something or ran out of time
                    burns.RemoveAt(i);
                    continue;
                }
                float* v = (float*)(projectile + velocityOffset);
                float speed = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                if (Plugin.LogFlights.Value)
                    LogFlight(burn, (float*)(projectile + positionOffset), speed, age);
                if (age >= burn.Until)
                {
                    burns.RemoveAt(i);
                    continue;
                }
                float a = burn.Motor.Acceleration(age, burn.Dry);
                if (a <= 0f || speed < 1f)
                    continue;
                float scale = 1f + a * dt / speed;
                v[0] *= scale;
                v[1] *= scale;
                v[2] *= scale;
            }
        }
    }

    /// <summary>
    /// Draws a <b>Rocket motor</b> picker under every round in the cannon
    /// panel's <b>Rounds</b> section and, with a motor, its fields and
    /// performance.
    /// </summary>
    internal static class MotorEditor
    {
        private static readonly string[] options = { "None", "Rocket" };

        // Cannon blueprint and round id, its fields and readouts
        private static readonly Dictionary<(IntPtr, string), (FloatProperty[] Fields, StringProperty[] Readouts)> properties = new();

        internal static void Draw(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed)
        {
            CannonBlueprint? gun = cannon.Blueprint;
            if (gun == null)
                return;
            bool has = Loadout.Get<RocketRound>(gun, id)?.Motor != null;
            EditorGui.Dropdown(drawer, "Rocket motor", options, has ? 1 : 0, i =>
            {
                Loadout.Edit<RocketRound>(gun, id, r => r.Motor = i == 0 ? null : r.Motor ?? new Motor());
                changed();
            }, "A rocket motor behind the round: the charge pushes it too, so it leaves slower, then speeds up in flight. The round's type is its warhead.");
            if (!has)
                return;
            if (!properties.TryGetValue((gun.Pointer, id), out var set))
                properties[(gun.Pointer, id)] = set = Make(gun, id, changed);
            foreach (FloatProperty field in set.Fields)
                drawer.Float(field);
            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }

        private static (FloatProperty[], StringProperty[]) Make(CannonBlueprint gun, string id, Action changed)
        {
            Burn B() => Plugin.BurnOf(gun, id) ?? default;
            // shortcut: readouts assume a full-bore shell's mass, not the round type's; pass each type's mass through rounds if they mislead
            float Shell() => ShellProperties.GetProjectileMass(gun.Caliber);
            float Launch() => gun.MuzzleVelocity * Loadout.VelocityRatio(gun, 1f + B().Mass / Shell());
            const string note = "With a full-bore shell as warhead, without drag or gravity.";
            StringProperty[] readouts =
            {
                EditorGui.Readout("Propellant", () => $"{B().Propellant:0.00} kg", "Fills the motor section."),
                EditorGui.Readout("Thrust", () => $"{B().Thrust / 1000f:0.0} kN", "The propellant's impulse over the burn time."),
                EditorGui.Readout("Launch velocity", () => $"{Launch():0} m/s", note),
                EditorGui.Readout("Burnout velocity", () => $"{Launch() + B().DeltaV(Shell() + B().Case):0} m/s", note),
                EditorGui.Readout("Burnout range", () => $"{B().BurnDistance(Launch(), Shell() + B().Case):0} m", note),
                EditorGui.Readout("Launch weight", () => $"{Shell() + B().Mass:0.00} kg", note),
            };
            FloatProperty Field(string name, Func<Motor, float> get, Action<Motor, float> set, string tooltip, float min, float max, float step) =>
                new(name, (Func<float>)(() => get(Loadout.Get<RocketRound>(gun, id)?.Motor ?? new Motor())), (Action<float>)(value =>
                {
                    Loadout.Edit<RocketRound>(gun, id, r =>
                    {
                        r.Motor ??= new Motor();
                        set(r.Motor, value);
                    });
                    changed();
                    EditorGui.Refresh(readouts);
                }), tooltip)
                { Min = min, Max = max, Step = step };
            FloatProperty[] fields =
            {
                Field("Motor length", m => m.Length, (m, v) => m.Length = v,
                    "Motor section length in calibers. Longer holds more propellant for more speed, but the charge pushes it too, so the round leaves slower, and its case is dead weight after burnout.", 0.25f, 10f, 0.25f),
                Field("Burn time", m => m.BurnTime, (m, v) => m.BurnTime = v,
                    "Seconds the motor burns. Same total push: short burns hard and reaches top speed close in, long burns gently and keeps pushing far out.", 0.1f, 10f, 0.1f),
                Field("Ignition delay", m => m.Delay, (m, v) => m.Delay = v,
                    "Seconds from launch until the motor lights. The round coasts at launch speed until then, so it reaches top speed further out.", 0f, 5f, 0.05f),
            };
            return (fields, readouts);
        }
    }
}
