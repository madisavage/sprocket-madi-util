using System;
using System.Numerics;
using Rounds;
using Xunit;

namespace Tests
{
    /// <summary>Finding the face a sim fragment ended on, from its ray and length, for NERA's angle.</summary>
    public class ArmourFace
    {
        // Two 1 m squares facing +z, at z = 0 and z = -0.01 (a 10 mm plate's faces), each two triangles
        private static readonly Vector3[] points =
        {
            new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0),
            new(-1, -1, -0.01f), new(1, -1, -0.01f), new(1, 1, -0.01f), new(-1, 1, -0.01f),
        };
        private static readonly int[] triangles = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

        [Fact]
        public void HeadOnFindsTheFaceAtTheDistance()
        {
            Vector3? n = ArmourMesh.Face(points, triangles, new Vector3(0.1f, 0.2f, 0.5f), -Vector3.UnitZ, 0.51f);
            Assert.NotNull(n);
            Assert.Equal(1f, MathF.Abs(n!.Value.Z), 4);
        }

        [Fact]
        public void SixtyDegreesGivesHalfCosine()
        {
            // 60 degrees off the normal, ending on the back face
            var d = new Vector3(MathF.Sin(MathF.PI / 3), 0, -MathF.Cos(MathF.PI / 3));
            // From 0.5 m in front of the front face's centre
            Vector3 origin = -d * (0.5f / MathF.Cos(MathF.PI / 3));
            float distance = 0.51f / MathF.Cos(MathF.PI / 3);
            Vector3? n = ArmourMesh.Face(points, triangles, origin, d, distance);
            Assert.NotNull(n);
            Assert.Equal(0.5f, MathF.Abs(Vector3.Dot(n!.Value, d)), 3);
        }

        [Fact]
        public void DistanceIsToTheNearestFaceAhead()
        {
            Assert.Equal(0.5f, ArmourMesh.Distance(points, triangles, new Vector3(0, 0, 0.5f), -Vector3.UnitZ), 4);
            // From inside the plate, the back face; facing away, nothing
            Assert.Equal(0.005f, ArmourMesh.Distance(points, triangles, new Vector3(0, 0, -0.005f), -Vector3.UnitZ), 4);
            Assert.Equal(float.PositiveInfinity, ArmourMesh.Distance(points, triangles, new Vector3(0, 0, 0.5f), Vector3.UnitZ));
        }

        [Fact]
        public void NoFaceNearTheDistanceIsNull() =>
            Assert.Null(ArmourMesh.Face(points, triangles, new Vector3(0, 0, 0.5f), -Vector3.UnitZ, 0.3f));
    }
}
