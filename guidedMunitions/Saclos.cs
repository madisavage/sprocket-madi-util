using System;
using System.Numerics;

namespace GuidedMunitions
{
    /// <summary>
    /// SACLOS steering: the round flies onto the gunner's line of sight, so it
    /// hits whatever the gunner keeps the sight on. Pure math, unit-tested.
    /// </summary>
    internal static class Saclos
    {
        /// <summary>Response of the guidance loop, rad/s: it settles onto the line in about 1/ω s, critically damped. Set from the <c>Response</c> config.</summary>
        internal static float Omega = 1.5f;

        internal const float Gravity = 9.81f;

        /// <summary>Half-angle of the sight's tracker, degrees: a round further off the sight line than this is lost.</summary>
        internal const float TrackerAngle = 15f;

        /// <summary>
        /// Whether the sight at <paramref name="origin"/> looking along unit <paramref name="direction"/>
        /// still sees a round at <paramref name="position"/>: within <see cref="TrackerAngle"/> of its line,
        /// or within <paramref name="near"/> m (leaving the muzzle, off to the side of the sight).
        /// Not behind it, so a sight turned away can't pull the round back onto its own vehicle.
        /// </summary>
        internal static bool InView(Vector3 position, Vector3 origin, Vector3 direction, float near = 20f)
        {
            Vector3 offset = position - origin;
            float distance = offset.Length();
            return distance <= near || Vector3.Dot(offset, direction) >= distance * MathF.Cos(TrackerAngle * MathF.PI / 180f);
        }

        /// <summary>
        /// The velocity after one step of <paramref name="dt"/> s for a round at
        /// <paramref name="position"/> flying at <paramref name="velocity"/>, steering onto the line
        /// from <paramref name="origin"/> along unit <paramref name="direction"/>. Its fins turn it
        /// at up to <paramref name="maxAccel"/> m/s² across its path, holding up its weight too; its speed is kept, as the
        /// motor and drag set that. Fin lift goes with speed squared, so below <paramref name="ratedSpeed"/> m/s
        /// (where <paramref name="maxAccel"/> is rated; 0 for any speed) it turns less, and a slow round can't hold itself up.
        /// Pulling costs speed: each m/s² of turn (holding up its weight too) loses 1/<paramref name="liftToDrag"/> m/s² to drag; 0 for none.
        /// </summary>
        internal static Vector3 Steer(Vector3 position, Vector3 velocity, Vector3 origin, Vector3 direction, float maxAccel, float dt, float ratedSpeed = 0f, float liftToDrag = 0f)
        {
            float speed = velocity.Length();
            if (ratedSpeed > 0f && speed < ratedSpeed)
                maxAccel *= speed * speed / (ratedSpeed * ratedSpeed);
            if (speed < 1f || maxAccel <= 0f)
                return velocity;
            Vector3 offset = position - origin;
            // Off the line, and drifting away from it (the line taken as still)
            Vector3 error = offset - Vector3.Dot(offset, direction) * direction;
            Vector3 drift = velocity - Vector3.Dot(velocity, direction) * direction;
            Vector3 command = -(Omega * Omega * error + 2f * Omega * drift) + new Vector3(0f, Gravity, 0f);
            Vector3 forward = velocity / speed;
            command -= Vector3.Dot(command, forward) * forward;
            float size = command.Length();
            if (size > maxAccel)
                command *= maxAccel / size;
            if (liftToDrag > 0f)
                speed = MathF.Max(speed - command.Length() / liftToDrag * dt, 0f);
            Vector3 turned = velocity + command * dt;
            return turned * (speed / turned.Length());
        }

        /// <summary>
        /// The ranges in m at which a round hits: fired from <paramref name="offset"/> m below the sight
        /// at a target on the sight line, as the gun aims at the crosshair, it passes within <paramref name="tolerance"/> m
        /// of the target. Each target range up to <paramref name="maxRange"/> is flown in 25 m steps, under gravity, with
        /// <paramref name="thrust"/> (m/s² along its path at each second after launch; null for none) and steering from
        /// <paramref name="ignition"/> s until <paramref name="burnout"/> s, as in <see cref="Steer"/>. Min is where the run of hits ending at the longest
        /// hit starts; both are infinity if none hit.
        /// </summary>
        internal static (float Min, float Max) HitRanges(float launch, float maxAccel, float maxRange, float ratedSpeed = 0f, float liftToDrag = 0f,
            float ignition = 0f, float burnout = float.PositiveInfinity, Func<float, float>? thrust = null, float offset = 2f, float tolerance = 0.5f)
        {
            const float step = 25f;
            int count = (int)(maxRange / step);
            var hits = new bool[count];
            for (int i = 0; i < count; i++)
                hits[i] = Miss((i + 1) * step) <= tolerance;
            int last = Array.LastIndexOf(hits, true);
            if (last < 0)
                return (float.PositiveInfinity, float.PositiveInfinity);
            int first = last;
            while (first > 0 && hits[first - 1])
                first--;
            return ((first + 1) * step, (last + 1) * step);

            float Miss(float range)
            {
                const float dt = 0.02f, seconds = 30f;
                var p = new Vector3(0f, -offset, 0f);
                Vector3 v = Vector3.Normalize(new Vector3(0f, offset, range)) * launch;
                var down = new Vector3(0f, -Gravity * dt, 0f);
                for (float t = 0f; t < seconds; t += dt)
                {
                    v += down;
                    Vector3 next = p + v * dt;
                    if (next.Z >= range)
                    {
                        Vector3 at = Vector3.Lerp(p, next, (range - p.Z) / (next.Z - p.Z));
                        return MathF.Sqrt(at.X * at.X + at.Y * at.Y);
                    }
                    p = next;
                    float speed = v.Length();
                    if (thrust != null && speed >= 1f)
                        v *= 1f + thrust(t) * dt / speed;
                    if (t >= ignition && t < burnout)
                        v = Steer(p, v, Vector3.Zero, Vector3.UnitZ, maxAccel, dt, ratedSpeed, liftToDrag);
                    if (v.Z <= 0f)
                        break;
                }
                return float.PositiveInfinity;
            }
        }
    }
}
