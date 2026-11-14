using System;
using System.Numerics;
using MadiLib;
using Xunit;

namespace Tests
{
    /// <summary>The shared flight simulator.</summary>
    public class TrajectoryTests
    {
        [Fact]
        public void VacuumFlightIsAParabola()
        {
            // 100 m/s level: at 500 m, 5 s in, dropped g t² / 2
            var end = Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, 100f), 500f, 60f, gravity: 9.81f);
            Assert.True(end.Crossed);
            Assert.Equal(5f, end.Time, 2);
            Assert.Equal(-9.81f * 25f / 2f, end.Position.Y, 1);
        }

        [Fact]
        public void ConstantThrustMatchesUniformAcceleration()
        {
            var end = Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, 10f), float.PositiveInfinity, 4f, 0.01f, thrust: _ => 5f);
            Assert.False(end.Crossed);
            Assert.Equal(4f, end.Time, 4);
            Assert.Equal(30f, end.Speed, 2);
            Assert.Equal(10f * 4f + 5f * 16f / 2f, end.Position.Z, 1);
        }

        [Fact]
        public void DragSlowsButNeverReverses()
        {
            var end = Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, 300f), 1000f, 60f, decel: v => 1e-3f * v * v);
            Assert.True(end.Crossed);
            Assert.True(end.Speed < 300f && end.Speed > 0f);
            // A huge drag stops it short instead of sending it back
            var stopped = Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, 300f), 1000f, 1f, decel: _ => 1e9f);
            Assert.False(stopped.Crossed);
            Assert.True(stopped.Position.Z >= 0f);
        }

        [Fact]
        public void SteeringRunsAfterEachStep()
        {
            int steps = 0;
            Trajectory.Fly(Vector3.Zero, new Vector3(0f, 0f, 100f), float.PositiveInfinity, 1f, 0.1f,
                steer: (float age, float dt, Vector3 p, ref Vector3 v) => steps++);
            Assert.InRange(steps, 10, 11);
        }
    }
}
