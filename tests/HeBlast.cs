using HighExplosive;
using Xunit;

namespace Tests
{
    public class HeBlast
    {
        [Theory]
        [InlineData(10f, 0.25f, 700)]
        [InlineData(25f, 0.25f, 700)]
        [InlineData(25f, 1f, 2000)]
        public void RaysPenetrateWhatTheyWereSolvedFor(float penetration, float kg, ushort k)
        {
            float speed = BlastRays.Speed(penetration, kg, k);
            float diameter = BlastRays.Diameter(kg) * 1000f;
            Assert.Equal(penetration, Rounds.Impacts.DeMarre(diameter, kg, speed, k), 2);
        }

        [Fact]
        public void FragmentHasTheMassOfASteelSphere()
        {
            float d = BlastRays.Diameter(1f);
            Assert.Equal(1f, 7850f * System.MathF.PI * d * d * d / 6f, 3);
        }
    }
}
