using System;

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
                if (Height(speed, angle, decel, range, gravity) is not float height)
                    return null;
                // Raising the gun by a small angle lifts the shell at that range by about range × angle
                angle -= height / range;
                if (MathF.Abs(height) < 0.01f)
                    return angle;
                if (angle > MathF.PI / 4f)
                    return null;
            }
            return angle;
        }

        /// <summary>The shell's height at <paramref name="range"/> m downrange, fired at <paramref name="angle"/>; null if it falls short.</summary>
        internal static float? Height(float speed, float angle, Func<float, float> decel, float range, float gravity)
        {
            float x = 0f, y = 0f, vx = speed * MathF.Cos(angle), vy = speed * MathF.Sin(angle);
            for (float t = 0f; t < MaxTime; t += Step)
            {
                float v = MathF.Sqrt(vx * vx + vy * vy);
                float d = v > 1e-6f ? decel(v) / v : 0f;
                vx -= vx * d * Step;
                vy -= (vy * d + gravity) * Step;
                float nx = x + vx * Step, ny = y + vy * Step;
                if (nx >= range)
                    return y + (ny - y) * (range - x) / (nx - x);
                if (vx <= 0f)
                    return null;
                x = nx;
                y = ny;
            }
            return null;
        }
    }
}
