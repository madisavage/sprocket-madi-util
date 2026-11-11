using System;
using RocketPropelled;
using Xunit;

namespace Tests
{
    /// <summary>The rocket motor's design and flight math, checked against the rocket equation.</summary>
    public class RocketMotor
    {
        [Fact]
        public void PropellantFillsTheSection()
        {
            // 100 mm, 2 calibers at 1000 kg/m³: π/4 · 0.1² · 0.2 · 1000
            Burn b = new Motor { Length = 2f, BurnTime = 2f }.Build(100f, 1000f, 0.5f, 2000f);
            Assert.Equal(MathF.PI / 4f * 0.01f * 0.2f * 1000f, b.Propellant, 4);
            Assert.Equal(b.Propellant / 2f, b.Case, 4);
            Assert.Equal(b.Propellant * 2000f / 2f, b.Thrust, 2);
        }

        [Fact]
        public void BurnTimeKeepsTheImpulse()
        {
            var motor = new Motor { Length = 3f };
            motor.BurnTime = 0.5f;
            float fast = motor.Build(75f, 1300f, 0.5f, 2000f).DeltaV(7f);
            motor.BurnTime = 4f;
            Assert.Equal(fast, motor.Build(75f, 1300f, 0.5f, 2000f).DeltaV(7f), 2);
        }

        [Fact]
        public void NoMotorLengthIsTheWarheadAtLaunchSpeed()
        {
            Burn b = new Motor { Length = 0f }.Build(75f, 1300f, 0.5f, 2000f);
            Assert.False(b.Burns);
            Assert.Equal(0f, b.Mass);
            Assert.Equal(0f, b.DeltaV(7f));
            Assert.Equal(300f, b.BurnDistance(300f, 7f) / b.BurnTime, 2);
        }

        [Fact]
        public void BurnMatchesTheRocketEquation()
        {
            // 5 kN for 2 s burning 2 kg on a 6 kg dry rocket: F t / P · ln(8 / 6)
            var b = new Burn(2f, 0f, 5000f, 2f);
            float expected = 5000f * 2f / 2f * MathF.Log(8f / 6f);
            Assert.Equal(expected, b.DeltaV(6f), 2);
            // Stepping the acceleration as the flight patch does lands on the same speed
            float v = 0f, dt = 0.001f;
            for (float t = 0f; t < 3f; t += dt)
                v += b.Acceleration(t + dt / 2f, 6f) * dt;
            Assert.Equal(expected, v, 0);
            Assert.Equal(5000f / 8f, b.Acceleration(0f, 6f), 3);
            Assert.Equal(0f, b.Acceleration(2f, 6f));
        }

        [Fact]
        public void DelayShiftsTheBurn()
        {
            var b = new Burn(2f, 0f, 5000f, 2f, 0.5f);
            Assert.Equal(0f, b.Acceleration(0.4f, 6f));
            Assert.Equal(5000f / 8f, b.Acceleration(0.5f, 6f), 3);
            Assert.Equal(0f, b.Acceleration(2.5f, 6f));
            // Coasts the delay at launch speed, then burns as without it
            Assert.Equal(new Burn(2f, 0f, 5000f, 2f).BurnDistance(300f, 6f) + 150f, b.BurnDistance(300f, 6f), 1);
        }

        [Fact]
        public void SustainerBurnsAfterTheBoost()
        {
            Burn b = new Motor { Length = 1f, BurnTime = 1f, SustainLength = 2f, SustainTime = 10f }.Build(100f, 1000f, 0.5f, 2000f);
            Assert.Equal(11f, b.BurnOut, 4);
            Assert.Equal((b.Propellant + b.SustainPropellant) / 2f, b.Case, 4);
            // The boost carries the sustainer's propellant; the sustainer pushes gently
            Assert.Equal(b.Thrust / (6f + b.SustainPropellant + b.Propellant), b.Acceleration(0f, 6f), 3);
            Assert.True(b.Acceleration(5f, 6f) < b.Acceleration(0.5f, 6f));
            Assert.Equal(0f, b.Acceleration(11f, 6f));
            // Two stages of the rocket equation: integrate the burn and compare
            float v = 0f, dt = 0.0005f;
            for (float t = 0f; t < 11f; t += dt)
                v += b.Acceleration(t + dt / 2f, 6f) * dt;
            Assert.Equal(v, b.DeltaV(6f), 0);
        }

        [Fact]
        public void NoSustainerNoSustainTime()
        {
            Burn b = new Motor { Length = 2f, BurnTime = 2f }.Build(100f, 1000f, 0.5f, 2000f);
            Assert.Equal(2f, b.BurnOut);
            Assert.Equal(0f, b.SustainPropellant);
        }
    }
}
