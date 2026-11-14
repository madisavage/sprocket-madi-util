extern alias UnityCoreModule;

using System;
using MadiLib;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using UnityObject = UnityCoreModule::UnityEngine.Object;
using UnityVector3 = UnityCoreModule::UnityEngine.Vector3;
using Transform = UnityCoreModule::UnityEngine.Transform;
using Vector3 = System.Numerics.Vector3;

namespace Rounds
{
    /// <summary>
    /// How far an AI gun's target is, and how fast and soon a round gets there:
    /// the AI rates rounds and leads rockets by these.
    /// </summary>
    /// <remarks>
    /// Not the AI's own target: its <c>ShortTargetInfo</c> threw when read through
    /// the interop (guidedMunitions, 2026-11-10). The target is taken as the
    /// enemy vehicle nearest the gun's aim instead.
    /// </remarks>
    internal static class AiRange
    {
        // Widest angle off the gun's aim a vehicle can be to be taken as its target, degrees
        private const float MaxOffAngle = 15f;
        // Nearest a vehicle can be to be taken as the target, m: closer is likely a wreck or a neighbour, and a rocket's
        // speed there is its launch speed, which would have the AI loft its shot at a real target far off
        private const float MinRange = 50f;

        /// <summary>
        /// Metres to the live vehicle on another team than <paramref name="cannon"/>'s nearest its aim, within
        /// <see cref="MaxOffAngle"/> and past <see cref="MinRange"/>; null for none.
        /// </summary>
        internal static float? Of(Cannon cannon)
        {
            Vehicle? own = cannon.GetComponentInParent<Vehicle>();
            if (own?.behaviour is not { } ours || ours == null)
                return null;
            UnityVector3 from = cannon.transform.position, forward = cannon.transform.forward;
            float best = MaxOffAngle;
            float? range = null;
            foreach (Vehicle vehicle in UnityObject.FindObjectsOfType<Vehicle>())
            {
                // Without a behaviour it's a wreck or not spawned: no team to tell it by
                if (vehicle == null || vehicle.Pointer == own.Pointer || vehicle.behaviour is not { } theirs || theirs == null || theirs.ID.TeamID == ours.ID.TeamID)
                    continue;
                UnityVector3 to = vehicle.transform.position - from;
                if (to.magnitude < MinRange)
                    continue;
                float off = UnityVector3.Angle(forward, to);
                if (off < best)
                {
                    best = off;
                    range = to.magnitude;
                }
            }
            return range;
        }

        /// <summary>
        /// A round's speed (m/s) and flight time (s) at <paramref name="range"/> m, fired at
        /// <paramref name="launch"/> m/s with <paramref name="def"/>'s drag (on its mass, as the game
        /// does) and <paramref name="thrust"/>; null if it doesn't get there in 60 s.
        /// </summary>
        internal static (float Speed, float Time)? At(ProjectileDefinition def, float launch, float range, Func<float, float>? thrust)
        {
            float mass = def.Mass;
            // Level and without gravity: drop barely changes the speed along the path
            var end = Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, launch), range, 60f, 0.01f,
                decel: v => mass > 0f ? def.CalculateDragForce(v) / mass : 0f, thrust: thrust);
            return end.Crossed ? (end.Speed, end.Time) : null;
        }
    }
}

namespace Rounds
{
    /// <summary>
    /// Has each AI gunner lead and drop its shots by its cannon's muzzle velocity
    /// (<see cref="AiVelocity"/>: the loaded round's mean speed to its target), not the
    /// full-bore shell's speed it was made with.
    /// </summary>
    /// <remarks>
    /// AI guns are laid by <c>GunLayer.AimAtDirection</c> from <c>GunLayerAI</c>, which works out
    /// lead and drop by its own <c>LeadSpeed</c>. Neither the cannon's muzzle velocity nor the
    /// <c>GunLayer</c>'s <c>projectileSpeed</c> reach it: left at the shell's 918 m/s, a rocket
    /// averaging ~1665 m/s to 2 km flew ~13 m over its target (logs, 2026-11-14).
    /// </remarks>
    [HarmonyLib.HarmonyPatch(typeof(Sprocket.ArtificialIntelligence.GunLayerAI), nameof(Sprocket.ArtificialIntelligence.GunLayerAI.Step))]
    internal static class GunLayerAiLead
    {
        // AI gunner, the cannon its layer lays (null if none, or the player's)
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Cannon?> cannons = new();

        private static void Prefix(Sprocket.ArtificialIntelligence.GunLayerAI __instance)
        {
            if (!cannons.TryGetValue(__instance.Pointer, out Cannon? cannon))
                cannons[__instance.Pointer] = cannon = Of(__instance);
            // A destroyed cannon reads as null
            if (cannon != null && cannon.muzzleVelocity > 0f)
                __instance.LeadSpeed = cannon.muzzleVelocity;
        }

        /// <summary>The cannon <paramref name="ai"/>'s gun layer lays; null if none is found or it's on the player's vehicle.</summary>
        private static Cannon? Of(Sprocket.ArtificialIntelligence.GunLayerAI ai)
        {
            var aimable = ai.Layer?.TryCast<Sprocket.Vehicles.CrewSystems.GunLayer>()?.aimable;
            // The aimable is a turret's or laying drive's behaviour: plain objects holding their transform
            var mount = aimable?.TryCast<Sprocket.Vehicles.Weapons.TurretBehaviour>()?.transform
                ?? aimable?.TryCast<Sprocket.Vehicles.Weapons.LayingDriveBehaviour>()?.trunnions;
            Transform? player = PlayerVehicle.Root;
            return mount == null || player != null && mount.IsChildOf(player) ? null : mount.GetComponentInChildren<Cannon>();
        }
    }
}
