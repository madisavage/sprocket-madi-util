using System;

namespace RocketPropelled
{
    /// <summary>The fields rocket adds to a round of any type, as stored in the vehicle file through <see cref="Rounds.Loadout"/>.</summary>
    internal sealed class RocketRound
    {
        /// <summary>The round's rocket motor; null for none.</summary>
        public Motor? Motor { get; set; }
    }

    /// <summary>
    /// A rocket motor's design: a full-bore section <see cref="Length"/>
    /// calibers long, burning for <see cref="BurnTime"/>. Everything else
    /// follows from it and the gun's caliber (see <see cref="Build"/>).
    /// </summary>
    internal sealed class Motor
    {
        /// <summary>Motor section length in calibers.</summary>
        public float Length { get; set; } = 2f;

        /// <summary>Seconds the motor burns: the same impulse, as high thrust briefly or low thrust for long.</summary>
        public float BurnTime { get; set; } = 1f;

        /// <summary>Seconds from launch to ignition, coasting at launch speed until then.</summary>
        public float Delay { get; set; }

        /// <summary>
        /// The motor in a gun of <paramref name="caliber"/> mm: propellant fills
        /// its section at <paramref name="density"/> kg/m³, its case weighs
        /// <paramref name="caseFraction"/> of the propellant, and every kg of
        /// propellant gives <paramref name="exhaust"/> N·s.
        /// </summary>
        internal Burn Build(float caliber, float density, float caseFraction, float exhaust)
        {
            float d = caliber / 1000f;
            float propellant = MathF.PI / 4f * d * d * d * Math.Max(Length, 0f) * density;
            return new Burn(propellant, propellant * caseFraction, BurnTime > 0f ? propellant * exhaust / BurnTime : 0f, Math.Max(BurnTime, 0f), Math.Max(Delay, 0f));
        }
    }

    /// <summary>
    /// A built motor: <see cref="Propellant"/> and <see cref="Case"/> in kg,
    /// <see cref="Thrust"/> in N for <see cref="BurnTime"/> s from <see cref="Delay"/> s after launch,
    /// burning propellant at a steady rate. The projectile's dry mass is its
    /// warhead's plus the case. Not tied to a cannon, so a guided missile can
    /// fly on the same motor.
    /// </summary>
    internal readonly record struct Burn(float Propellant, float Case, float Thrust, float BurnTime, float Delay = 0f)
    {
        /// <summary>Seconds from launch the motor burns out.</summary>
        internal float BurnOut => Delay + BurnTime;

        /// <summary>Seconds after launch to log a rocket's flight and its sight prediction at: burnout, then each second to 3.</summary>
        internal float[] Checkpoints => new[] { BurnOut, 1f, 2f, 3f };

        /// <summary>Whether the motor pushes at all.</summary>
        internal bool Burns => Thrust > 0f && BurnTime > 0f;

        /// <summary>Motor mass at launch, kg.</summary>
        internal float Mass => Propellant + Case;

        /// <summary>Acceleration in m/s² <paramref name="age"/> s after launch, for a projectile of <paramref name="dry"/> kg burnt out.</summary>
        internal float Acceleration(float age, float dry) =>
            !Burns || age < Delay || age >= BurnOut ? 0f : Thrust / (dry + Propellant * (1f - (age - Delay) / BurnTime));

        /// <summary>Speed gained over the burn, m/s, without drag or gravity: the rocket equation at a steady burn rate.</summary>
        internal float DeltaV(float dry) =>
            !Burns || dry <= 0f ? 0f : Thrust * BurnTime / Propellant * MathF.Log((dry + Propellant) / dry);

        /// <summary>Distance flown from launch at <paramref name="v0"/> m/s to burnout, m, without drag or gravity.</summary>
        internal float BurnDistance(float v0, float dry)
        {
            const int steps = 200;
            float dt = BurnTime / steps, v = v0, x = v0 * Delay;
            for (int i = 0; i < steps; i++)
            {
                float a = Acceleration(Delay + (i + 0.5f) * dt, dry);
                x += (v + 0.5f * a * dt) * dt;
                v += a * dt;
            }
            return x;
        }
    }
}
