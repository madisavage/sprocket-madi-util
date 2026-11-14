using System;
using System.Numerics;
using Composite;
using Xunit;

namespace Tests
{
    /// <summary>Composite's per-vertex stacking direction must never come out NaN.</summary>
    public class Stacking
    {
        [Fact]
        public void OppositeNormalsThrow()
        {
            Assert.Throws<InvalidOperationException>(() => FreeformEditGuiPatch.Direction(new[] { Vector3.UnitY, -Vector3.UnitY }, null));
        }

        [Fact]
        public void NanNormalIsSkipped()
        {
            var nan = new Vector3(float.NaN, 0f, 0f);
            Assert.Equal(Vector3.UnitY, FreeformEditGuiPatch.Direction(new[] { Vector3.UnitY, nan, Vector3.Zero }, null));
        }

        [Fact]
        public void FlatFaceGivesItsNormal()
        {
            Assert.Equal(Vector3.UnitZ, FreeformEditGuiPatch.Direction(new[] { Vector3.UnitZ, Vector3.UnitZ }, null));
        }

        [Fact]
        public void AxisPointsTowardFaces()
        {
            Assert.Equal(-Vector3.UnitX * 2f, FreeformEditGuiPatch.Direction(new[] { -Vector3.UnitX }, Vector3.UnitX, 0.5f));
        }
    }
}
