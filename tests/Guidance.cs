using System;
using System.Numerics;
using GuidedMunitions;
using Xunit;

namespace Tests
{
    /// <summary>SACLOS steering: settles onto the sight line under gravity, within its turn limit, keeping its speed.</summary>
    public class SaclosGuidance
    {
        private const float Dt = 0.02f;
        private static readonly Vector3 Down = new(0f, -Saclos.Gravity, 0f);

        /// <summary>Flies <paramref name="seconds"/> as the game does (gravity, then a move), steering after each step; the last position.</summary>
        private static Vector3 Fly(Vector3 p, Vector3 v, Vector3 origin, Vector3 direction, float maxAccel, float seconds)
        {
            for (float t = 0f; t < seconds; t += Dt)
            {
                v += Down * Dt;
                p += v * Dt;
                v = Saclos.Steer(p, v, origin, direction, maxAccel, Dt);
            }
            return p;
        }

        [Fact]
        public void SettlesOntoTheLineUnderGravity()
        {
            // Launched 5 m below and 1 m left of a sight line straight ahead, at 200 m/s
            Vector3 p = Fly(new Vector3(-1f, -5f, 0f), new Vector3(0f, 0f, 200f), Vector3.Zero, Vector3.UnitZ, 10f * Saclos.Gravity, 4f);
            Assert.True(MathF.Sqrt(p.X * p.X + p.Y * p.Y) < 0.3f, $"off the line by {p.X}, {p.Y}");
            Assert.True(p.Z > 700f);
        }

        [Fact]
        public void FollowsATurnedLine()
        {
            // The gunner swings 10° right after launch
            Vector3 direction = Vector3.Normalize(new Vector3(MathF.Tan(10f * MathF.PI / 180f), 0f, 1f));
            Vector3 p = Fly(Vector3.Zero, new Vector3(0f, 0f, 200f), Vector3.Zero, direction, 10f * Saclos.Gravity, 5f);
            Vector3 off = p - Vector3.Dot(p, direction) * direction;
            Assert.True(off.Length() < 0.5f, $"off the line by {off.Length()} m");
        }

        [Fact]
        public void TurnsNoFasterThanItsLimitAndKeepsItsSpeed()
        {
            float maxAccel = 2f * Saclos.Gravity;
            var v = new Vector3(0f, 0f, 200f);
            // Far off a line at right angles: the command saturates
            Vector3 turned = Saclos.Steer(new Vector3(0f, 0f, 100f), v, new Vector3(500f, 0f, 0f), Vector3.UnitX, maxAccel, Dt);
            Assert.Equal(v.Length(), turned.Length(), 3);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(v, turned) / (v.Length() * turned.Length()), -1f, 1f));
            Assert.True(angle <= maxAccel * Dt / v.Length() * 1.001f, $"turned {angle} rad");
        }

        [Fact]
        public void HitsOutToTheWireOnceSettled()
        {
            var (min, max) = Saclos.HitRanges(150f, 10f * Saclos.Gravity, 3000f, 150f, 4f);
            Assert.InRange(min, 25f, 500f);
            Assert.Equal(3000f, max);
        }

        [Fact]
        public void FasterAndSlowerResponseSettleFurtherOut()
        {
            float slow = Saclos.HitRanges(150f, 10f * Saclos.Gravity, 3000f, 150f, 4f).Min;
            Assert.True(Saclos.HitRanges(500f, 10f * Saclos.Gravity, 3000f, 500f, 4f).Min > slow);
            Assert.True(Saclos.HitRanges(150f, 10f * Saclos.Gravity, 3000f, 150f, 4f, 0.5f).Min > slow);
        }

        [Fact]
        public void CoastsGuidedAfterBurnout()
        {
            // A TOW-like flight: a 1.5 s boost to 300 m/s, then 20 s coasting, guided all the way out
            float Boost(float t) => t < 1.5f ? 150f : 0f;
            Assert.Equal(3000f, Saclos.HitRanges(75f, 10f * Saclos.Gravity, 3000f, 300f, 4f, thrust: Boost).Max);
        }

        [Theory]
        [InlineData(10f, 0f)]
        [InlineData(0f, 10f)]
        public void KeepsUpWithAMovingLine(float crossing, float shooter)
        {
            // The gunner tracks a target crossing at 2 km, or drives sideways under a still one; without following the line it misses by ~2v/ω, 13 m at 10 m/s
            const float range = 2000f;
            var motion = new LineMotion();
            Vector3 p = Vector3.Zero, v = new(0f, 0f, 250f), target = new(0f, 0f, range);
            float t = 0f;
            while (p.Z < range)
            {
                t += Dt;
                Vector3 origin = new(shooter * t, 0f, 0f), at = target + new Vector3(crossing * t, 0f, 0f);
                Vector3 direction = Vector3.Normalize(at - origin);
                motion.Update(origin, direction, Dt);
                v += Down * Dt;
                p += v * Dt;
                v = Saclos.Steer(p, v, origin, direction, 10f * Saclos.Gravity, Dt, 0f, 0f, Saclos.DefaultOmega, motion.OriginVelocity, motion.TurnRate);
            }
            float miss = MathF.Abs(p.X - (target.X + crossing * t));
            Assert.True(miss < 1f, $"missed by {miss} m");
        }

        [Fact]
        public void TooWeakToHoldItsWeightOnlyHitsCloseIn() =>
            Assert.InRange(Saclos.HitRanges(200f, 0.5f * Saclos.Gravity, 3000f, 200f, 4f).Max, 25f, 300f);

        [Fact]
        public void SlowRoundCantHoldItsWeight()
        {
            // 10 g rated at 300 m/s is under 1 g at 90 m/s: level, it sinks below the line it holds at speed
            var p = new Vector3(0f, 0f, 0f);
            Vector3 Fly(float speed)
            {
                Vector3 pos = p, v = new(0f, 0f, speed);
                for (int i = 0; i < 100; i++)
                {
                    v += new Vector3(0f, -Saclos.Gravity * Dt, 0f);
                    pos += v * Dt;
                    v = Saclos.Steer(pos, v, Vector3.Zero, Vector3.UnitZ, 10f * Saclos.Gravity, Dt, 300f);
                }
                return pos;
            }
            Assert.InRange(Fly(300f).Y, -0.5f, 0.5f);
            Assert.True(Fly(90f).Y < -1f, $"{Fly(90f).Y} m at 90 m/s");
        }

        [Fact]
        public void SightTurnedAwayLosesTheRound()
        {
            var ahead = new Vector3(0f, 0f, 500f);
            Assert.True(Saclos.InView(ahead, Vector3.Zero, Vector3.UnitZ));
            // Turned around: the line behind the sight would run back through its own vehicle
            Assert.False(Saclos.InView(ahead, Vector3.Zero, -Vector3.UnitZ));
            // Turned 30° off: outside the tracker
            Assert.False(Saclos.InView(ahead, Vector3.Zero, Vector3.Normalize(new Vector3(0.5f, 0f, 0.866f))));
            // Leaving the muzzle beside the sight
            Assert.True(Saclos.InView(new Vector3(2f, -2f, 1f), Vector3.Zero, Vector3.UnitZ));
        }

        [Fact]
        public void HoldingItsWeightCostsSpeed()
        {
            var v = new Vector3(0f, 0f, 200f);
            // On the line, level: the fins only hold up its weight, 1 g, which at L/D 4 costs g/4 m/s² of speed
            Vector3 after = Saclos.Steer(Vector3.Zero, v, Vector3.Zero, Vector3.UnitZ, 10f * Saclos.Gravity, 1f, 0f, 4f);
            Assert.Equal(200f - Saclos.Gravity / 4f, after.Length(), 2);
        }

        [Fact]
        public void NoTurnLimitNoSteering()
        {
            var v = new Vector3(3f, 1f, 200f);
            Assert.Equal(v, Saclos.Steer(new Vector3(0f, -5f, 0f), v, Vector3.Zero, Vector3.UnitZ, 0f, Dt));
        }
    }
}
