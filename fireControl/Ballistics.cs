using System;
using System.Numerics;
using MadiLib;

namespace FireControl
{
    /// <summary>Superelevation from a shell's speed and drag, by simulating its flight.</summary>
    internal static class Ballistics
    {
        private const float Step = 0.005f, MaxTime = 60f;

        /// <summary>
        /// The angle (radians) to raise the gun above the line of sight so a
        /// shell fired at <paramref name="speed"/> m/s crosses that line again
        /// <paramref name="range"/> m away; null if it can't reach it.
        /// The line is taken as level (the rifleman's rule).
        /// </summary>
        /// <param name="decel">Drag deceleration (m/s²) at a speed (m/s).</param>
        /// <param name="gravity">m/s², positive.</param>
        internal static float? Elevation(float speed, Func<float, float> decel, float range, float gravity)
        {
            if (speed <= 0f || range <= 0f)
                return 0f;
            float angle = 0f;
            for (int i = 0; i < 8; i++)
            {
                float? height = Height(speed, angle, decel, range, gravity);
                // Falls short
                if (height == null)
                    return null;
                // Raising the gun by a small angle lifts the shell at that range by about range × angle
                angle -= height.Value / range;
                if (MathF.Abs(height.Value) < 0.01f)
                    return angle;
                if (angle > MathF.PI / 4f)
                    return null;
            }
            return angle;
        }

        /// <summary>The shell's height at <paramref name="range"/> m downrange, fired at <paramref name="angle"/>; null if it falls short.</summary>
        internal static float? Height(float speed, float angle, Func<float, float> decel, float range, float gravity)
        {
            Trajectory.State end = Trajectory.Fly(Vector3.Zero, new Vector3(0f, MathF.Sin(angle), MathF.Cos(angle)) * speed, range, MaxTime, Step, gravity, decel);
            return end.Crossed ? end.Position.Y : null;
        }
    }
}
