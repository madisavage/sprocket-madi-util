using System;
using FireControl;
using Xunit;

namespace Tests
{
    /// <summary>fireControl's superelevation solver.</summary>
    public class FireControlTests
    {
        private const float G = 9.81f;

        [Theory]
        [InlineData(800f, 1000f)]
        [InlineData(300f, 2000f)]
        public void WithoutDragMatchesVacuumFormula(float speed, float range)
        {
            float expected = 0.5f * MathF.Asin(G * range / (speed * speed));
            float? angle = Ballistics.Elevation(speed, _ => 0f, range, G);
            Assert.NotNull(angle);
            Assert.Equal(expected, angle!.Value, 3);
        }

        [Fact]
        public void DragNeedsMoreElevation()
        {
            float vacuum = Ballistics.Elevation(800f, _ => 0f, 1500f, G)!.Value;
            float dragged = Ballistics.Elevation(800f, v => 1e-4f * v * v, 1500f, G)!.Value;
            Assert.True(dragged > vacuum * 1.1f, $"{dragged} vs {vacuum}");
            Assert.Equal(0f, Ballistics.Height(800f, dragged, v => 1e-4f * v * v, 1500f, G)!.Value, 1);
        }

        [Fact]
        public void OutOfReachIsNull() => Assert.Null(Ballistics.Elevation(100f, _ => 0f, 5000f, G));
    }
}
