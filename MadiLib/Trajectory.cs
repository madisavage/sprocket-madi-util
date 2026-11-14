using System;
using System.Numerics;

namespace MadiLib
{
    /// <summary>
    /// One flight simulator for every estimate of a projectile's path: fire
    /// control's superelevation, guided rounds' "Hits at", rocket burn distance and
    /// the AI's speed at range. Pure (no game types), so it's unit tested.
    /// </summary>
    /// <remarks>
    /// Downrange is +Z, up is +Y. Each step the velocity takes gravity, the
    /// thrust along the path and the drag against it, then the position moves
    /// at the step's mean velocity, then <c>steer</c> may change the velocity (a guided round's fins).
    /// </remarks>
    public static class Trajectory
    {
        /// <summary>Changes <paramref name="velocity"/> after a step that ended <paramref name="age"/> s after launch at <paramref name="position"/>.</summary>
        public delegate void Steering(float age, float dt, Vector3 position, ref Vector3 velocity);

        /// <summary>Where a flight ended: its position, velocity and age (s), and whether it crossed the range asked for.</summary>
        public readonly record struct State(Vector3 Position, Vector3 Velocity, float Time, bool Crossed)
        {
            /// <summary>Speed, m/s.</summary>
            public float Speed => Velocity.Length();
        }

        /// <summary>
        /// Flies from <paramref name="position"/> at <paramref name="velocity"/> until it
        /// crosses <paramref name="range"/> m downrange (its state there, interpolated), turns
        /// back, or <paramref name="maxTime"/> s pass (its last state, not crossed).
        /// </summary>
        /// <param name="position">Start, m.</param>
        /// <param name="velocity">Start velocity, m/s.</param>
        /// <param name="range">Downrange distance (along +Z) to stop at, m; infinity to fly for <paramref name="maxTime"/>.</param>
        /// <param name="maxTime">Longest flight, s.</param>
        /// <param name="dt">Step, s.</param>
        /// <param name="gravity">m/s², positive; 0 for none.</param>
        /// <param name="decel">Drag deceleration (m/s²) at a speed (m/s); null for none.</param>
        /// <param name="thrust">Acceleration along the path (m/s²) at an age (s); null for none.</param>
        /// <param name="steer">Called after each step; null for an unguided flight.</param>
        public static State Fly(Vector3 position, Vector3 velocity, float range, float maxTime, float dt = 0.005f, float gravity = 0f,
            Func<float, float>? decel = null, Func<float, float>? thrust = null, Steering? steer = null)
        {
            Vector3 p = position, v = velocity;
            float t = 0f;
            while (t < maxTime)
            {
                // The last step is cut short, so a flight ends at maxTime exactly
                float h = MathF.Min(dt, maxTime - t);
                Vector3 before = v;
                float speed = v.Length();
                if (speed > 1e-6f)
                {
                    // Thrust at mid-step, so a burn's start and end land between steps right
                    float along = (thrust?.Invoke(t + h / 2f) ?? 0f) - (decel?.Invoke(speed) ?? 0f);
                    // Drag never reverses the flight within a step
                    v *= MathF.Max(0f, 1f + along * h / speed);
                }
                v.Y -= gravity * h;
                // Moves at the step's mean velocity
                Vector3 next = p + (before + v) * (h / 2f);
                if (next.Z >= range)
                {
                    float f = next.Z > p.Z ? (range - p.Z) / (next.Z - p.Z) : 0f;
                    return new State(Vector3.Lerp(p, next, f), v, t + f * h, true);
                }
                p = next;
                t += h;
                steer?.Invoke(t, h, p, ref v);
                if (v.Z <= 0f)
                    break;
            }
            return new State(p, v, t, false);
        }
    }
}
