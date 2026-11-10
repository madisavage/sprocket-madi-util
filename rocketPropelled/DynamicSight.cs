using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Rounds;

namespace RocketPropelled
{
    /// <summary>
    /// Compatibility with Dynamic Gun Sight (sprocket.dynamicsight): its
    /// range ladder flies rockets under their motor. Its
    /// <c>Trajectory.Simulate</c> steps a shell by gravity and
    /// <c>DragTable.Decel(speed)</c>, called once a step and nowhere else; while it
    /// simulates a rocket's drag table, the motor's acceleration at that step
    /// is taken off the drag, so it pushes along the path as in flight.
    /// </summary>
    /// <remarks>
    /// The sight makes one drag table per projectile type, in
    /// <c>AmmoCatalog.Load(typeId)</c>; rounds points that at the loaded
    /// round's type, so the table is tied to its round there. Its muzzle
    /// velocity is the launch velocity (predicted from the charge, or
    /// measured at launch), which is where the motor starts.
    /// </remarks>
    internal static class DynamicSight
    {
        // A rocket's drag table, its motor, dry mass and launch velocity
        private static readonly ConditionalWeakTable<object, Tuple<Burn, float, float>> rockets = new();
        // The rocket being simulated, its time step and the steps taken
        private static Tuple<Burn, float, float>? active;
        private static object? activeDrag;
        // Drag tables and launch speeds whose prediction is logged
        private static readonly HashSet<(object, int)> logged = new();
        private static double launchSpeed;
        private static double step;
        private static int steps;

        internal static void Patch(Harmony harmony)
        {
            var load = AccessTools.Method("SprocketDynamicSight.Game.AmmoCatalog:Load");
            var simulate = AccessTools.Method("SprocketDynamicSight.Core.Trajectory:Simulate");
            var decel = AccessTools.Method("SprocketDynamicSight.Core.DragTable:Decel");
            var predict = AccessTools.Method("SprocketDynamicSight.Game.AmmoCatalog:TryPredictVelocity");
            if (load == null || simulate == null || decel == null || predict == null)
            {
                Plugin.Logger.LogWarning("Dynamic Gun Sight has changed; its range ladder flies rockets without their motor.");
                return;
            }
            harmony.Patch(load, postfix: new HarmonyMethod(typeof(DynamicSight), nameof(LoadPostfix)));
            harmony.Patch(simulate, prefix: new HarmonyMethod(typeof(DynamicSight), nameof(SimulatePrefix)),
                finalizer: new HarmonyMethod(typeof(DynamicSight), nameof(SimulateFinalizer)));
            harmony.Patch(decel, postfix: new HarmonyMethod(typeof(DynamicSight), nameof(DecelPostfix)));
            harmony.Patch(predict, postfix: new HarmonyMethod(typeof(DynamicSight), nameof(PredictPostfix)));
        }

        /// <summary><c>RoundInfo Load(int typeId)</c>: ties a rocket's drag table to its motor.</summary>
        private static void LoadPostfix(int typeId, object? __result)
        {
            if (__result == null || Traverse.Create(__result).Field("Drag").GetValue() is not { } drag
                || Loadout.RoundOfProjectile(typeId) is not { } round || Plugin.BurnOf(round.Owner, round.Id) is not { Burns: true } burn)
                return;
            rockets.AddOrUpdate(drag, Tuple.Create(burn, round.Mass + burn.Case, Plugin.LaunchSpeeds.GetValueOrDefault((round.Owner.Pointer, round.Id))));
        }

        /// <summary>
        /// <c>TryPredictVelocity(RoundInfo info, CannonBehaviour)</c>: the sight
        /// drops predictions and measurements under 20 m/s and falls back to
        /// the cannon's velocity, which for a rocket is the AI's burnout lead;
        /// a rocket's prediction is its launch velocity instead.
        /// </summary>
        private static void PredictPostfix(object info)
        {
            var round = Traverse.Create(info);
            if (round.Field("Drag").GetValue() is { } drag && rockets.TryGetValue(drag, out var rocket) && rocket.Item3 > 0f
                && round.Field<float>("PredictedVelocity").Value <= 0f)
                round.Field<float>("PredictedVelocity").Value = rocket.Item3;
        }

        private static void SimulatePrefix(object drag, double dt)
        {
            active = rockets.TryGetValue(drag, out var rocket) ? rocket : null;
            activeDrag = drag;
            step = dt;
            steps = 0;
        }

        private static Exception? SimulateFinalizer(Exception? __exception, object __instance)
        {
            // At most once per drag table and launch speed: the sight simulates every frame
            if (active != null && Plugin.LogFlights.Value && __exception == null && logged.Add((activeDrag!, (int)launchSpeed)))
                LogPrediction(__instance, active.Item1);
            active = null;
            return __exception;
        }

        /// <summary>Logs the simulated path at <see cref="Burn.Checkpoints"/>, as <see cref="Flight"/> logs the real one.</summary>
        private static void LogPrediction(object trajectory, Burn burn)
        {
            var t = Traverse.Create(trajectory);
            int count = t.Field<int>("Count").Value;
            double[] times = t.Field<double[]>("T").Value;
            Array points = t.Field<Array>("P").Value;
            (double X, double Y, double Z) At(int i)
            {
                var p = Traverse.Create(points.GetValue(i));
                return (p.Field<double>("x").Value, p.Field<double>("y").Value, p.Field<double>("z").Value);
            }
            var start = At(0);
            Plugin.Logger.LogInfo($"Sight prediction: {launchSpeed:0} m/s at launch, step {step:0.0000} s");
            foreach (float checkpoint in burn.Checkpoints)
                for (int i = 1; i < count; i++)
                    if (times[i] >= checkpoint)
                    {
                        var (x, y, z) = At(i);
                        var (px, py, pz) = At(i - 1);
                        double dt = times[i] - times[i - 1];
                        double speed = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz)) / dt;
                        Plugin.Logger.LogInfo($"Sight prediction at {times[i]:0.000} s: range {Math.Sqrt((x - start.X) * (x - start.X) + (z - start.Z) * (z - start.Z)):0.0} m, height {y - start.Y:0.00} m, {speed:0} m/s");
                        break;
                    }
        }

        /// <summary>Takes the motor's push off the drag at the step being simulated.</summary>
        private static void DecelPostfix(double speed, ref double __result)
        {
            if (active == null)
                return;
            if (steps == 0)
                launchSpeed = speed;
            __result -= active.Item1.Acceleration((float)(steps++ * step), active.Item2);
        }
    }
}
