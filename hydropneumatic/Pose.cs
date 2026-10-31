using System;
using System.Collections.Generic;

namespace Hydropneumatic
{
    /// <summary>
    /// The vehicle's pose on its suspension and how far each road wheel moves
    /// for it. Pure math, no game types, so it's unit-tested.
    /// </summary>
    /// <remarks>
    /// Arm positions are in metres from the centre of the wheels: x forward,
    /// y right. Offsets are in mm, positive raising the hull at that wheel.
    /// </remarks>
    internal static class Pose
    {
        /// <summary>Ride height (mm), pitch (degrees, front up) and roll (degrees, right up).</summary>
        internal readonly record struct State(float Height, float Pitch, float Roll);

        private const float Deg = MathF.PI / 180f;

        /// <summary>Hull lift in mm at an arm <paramref name="x"/> m forward and <paramref name="y"/> m right of the wheels' centre.</summary>
        internal static float Offset(State s, float x, float y) =>
            s.Height + 1000f * x * MathF.Tan(s.Pitch * Deg) + 1000f * y * MathF.Tan(s.Roll * Deg);

        /// <summary>The largest offset of any arm, for arms within <paramref name="halfLength"/> and <paramref name="halfWidth"/> m of the centre.</summary>
        internal static float Worst(State s, float halfLength, float halfWidth) =>
            MathF.Abs(s.Height) + 1000f * halfLength * MathF.Abs(MathF.Tan(s.Pitch * Deg)) + 1000f * halfWidth * MathF.Abs(MathF.Tan(s.Roll * Deg));

        /// <summary>
        /// <paramref name="s"/> moved by each delta in turn (height, pitch,
        /// roll), each clamped so no arm passes <paramref name="travel"/> mm
        /// with the other two as they are.
        /// </summary>
        internal static State Move(State s, float dHeight, float dPitch, float dRoll, float travel, float halfLength, float halfWidth)
        {
            float pitchLift = 1000f * halfLength * MathF.Abs(MathF.Tan(s.Pitch * Deg));
            float rollLift = 1000f * halfWidth * MathF.Abs(MathF.Tan(s.Roll * Deg));
            float height = Clamp(s.Height, s.Height + dHeight, travel - pitchLift - rollLift);

            float pitchLimit = MathF.Atan(MathF.Max(0f, travel - MathF.Abs(height) - rollLift) / (1000f * halfLength)) / Deg;
            float pitch = Clamp(s.Pitch, s.Pitch + dPitch, pitchLimit);
            pitchLift = 1000f * halfLength * MathF.Abs(MathF.Tan(pitch * Deg));

            float rollLimit = MathF.Atan(MathF.Max(0f, travel - MathF.Abs(height) - pitchLift) / (1000f * halfWidth)) / Deg;
            return new State(height, pitch, Clamp(s.Roll, s.Roll + dRoll, rollLimit));
        }

        /// <summary>
        /// <paramref name="value"/> within ±<paramref name="limit"/>, except a
        /// value already past the limit (<paramref name="old"/>) may stay or move
        /// back toward 0, never further out.
        /// </summary>
        private static float Clamp(float old, float value, float limit)
        {
            limit = MathF.Max(0f, limit);
            if (MathF.Abs(value) <= limit)
                return value;
            return MathF.Sign(value) * MathF.Max(limit, MathF.Min(MathF.Abs(value), MathF.Abs(old)));
        }

        /// <summary>
        /// <paramref name="s"/> moved toward <paramref name="target"/> for
        /// <paramref name="dt"/> s: the outermost wheel moves at most
        /// <paramref name="rate"/> mm/s for each of height, pitch and roll. No overshoot.
        /// </summary>
        internal static State Toward(State s, State target, float dt, float rate, float halfLength, float halfWidth, float travel = float.MaxValue)
        {
            float step = rate * dt;
            float Approach(float from, float to, float max) => from + Math.Clamp(to - from, -max, max);
            return Move(s,
                Approach(s.Height, target.Height, step) - s.Height,
                Approach(s.Pitch, target.Pitch, MathF.Atan(step / (1000f * halfLength)) / Deg) - s.Pitch,
                Approach(s.Roll, target.Roll, MathF.Atan(step / (1000f * halfWidth)) / Deg) - s.Roll,
                travel, halfLength, halfWidth);
        }

        /// <summary>
        /// Metres to add to a track's suspension preload for the height and
        /// roll of <paramref name="s"/>; the game lifts the hull 1 m per metre of
        /// preload. Pitch is left to <see cref="Struts"/>.
        /// </summary>
        /// <param name="side">The track's side: -1 left, 1 right.</param>
        /// <param name="halfWidth">Half the distance between the tracks, in metres.</param>
        internal static float Preload(State s, float side, float halfWidth) =>
            Offset(s with { Pitch = 0f }, 0f, side * halfWidth) / 1000f;

        /// <summary>
        /// The hull's pitch (degrees, nose up) against the line of its wheels:
        /// the least-squares slope of each wheel's compression along the hull.
        /// Nose up hangs the front wheels lower, so they're less compressed.
        /// </summary>
        /// <param name="wheels">Each wheel's position along the hull (m, forward) and compression (m, toward the hull).</param>
        /// <returns>0 when the wheels don't spread along the hull.</returns>
        internal static float TrackPitch(IReadOnlyList<(float X, float Compression)> wheels)
        {
            float meanX = 0f, meanC = 0f;
            foreach (var (x, c) in wheels)
            {
                meanX += x / wheels.Count;
                meanC += c / wheels.Count;
            }
            float covariance = 0f, variance = 0f;
            foreach (var (x, c) in wheels)
            {
                covariance += (x - meanX) * (c - meanC);
                variance += (x - meanX) * (x - meanX);
            }
            return variance < 1e-6f ? 0f : MathF.Atan(-covariance / variance) / Deg;
        }

        /// <summary>
        /// Strut forces (N, along the hull's up axis) at each loaded front and
        /// rear arm that pitch the hull by <paramref name="moment"/> (N m, nose
        /// up) with no net force, so the hull can't lift off.
        /// </summary>
        /// <remarks>
        /// The pull on the light end (pulled down: the rear for nose up, the
        /// front for nose down) is capped at its static share of the weight, so
        /// its wheels stay on the ground. Zero when either end has no loaded arm.
        /// </remarks>
        /// <param name="frontLever">Mean distance (m) of the loaded front arms ahead of the centre of mass.</param>
        /// <param name="rearLever">Mean distance (m) of the loaded rear arms behind it.</param>
        /// <param name="front">Loaded front arms.</param>
        /// <param name="rear">Loaded rear arms.</param>
        /// <param name="weight">The vehicle's weight in N.</param>
        /// <param name="arms">All arms, loaded or not.</param>
        internal static (float Front, float Rear, bool Capped) Struts(float moment, float frontLever, float rearLever, int front, int rear, float weight, int arms)
        {
            if (front == 0 || rear == 0 || arms == 0)
                return (0f, 0f, false);
            float pair = moment / (frontLever + rearLever);
            float limit = weight * (moment > 0f ? rear : front) / arms;
            bool capped = MathF.Abs(pair) > limit;
            if (capped)
                pair = MathF.Sign(pair) * limit;
            return (pair / front, -pair / rear, capped);
        }
    }
}
