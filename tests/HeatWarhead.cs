using HighExplosive;
using Xunit;

namespace Tests
{
    public class HeatWarhead
    {
        [Fact]
        public void BestStandoffAndDefaultConeGivePeak() =>
            Assert.Equal(4.5f, ShapedCharge.Penetration(ShapedCharge.BestStandoff, ShapedCharge.DefaultCone, 4.5f), 4);

        [Theory]
        [InlineData(0.5f)]
        [InlineData(6f)]
        public void StandoffEitherSideOfBestLosesPenetration(float standoff) =>
            Assert.True(ShapedCharge.StandoffFactor(standoff) < 1f);

        [Fact]
        public void NarrowConeGoesDeeperAndThinner()
        {
            Assert.True(ShapedCharge.ConeFactor(30f) > 1f);
            Assert.True(ShapedCharge.WidthFactor(30f) < 1f);
            Assert.True(ShapedCharge.ConeFactor(120f) < 1f);
            Assert.True(ShapedCharge.WidthFactor(120f) > 1f);
        }

        [Fact]
        public void NoseCostsOnlyPastOneCaliber()
        {
            Assert.Equal(1f, ShapedCharge.Nose(0.5f, 0.2f));
            Assert.Equal(1.3f, ShapedCharge.Nose(2.5f, 0.2f), 4);
        }

        [Theory]
        [InlineData(2f)]
        [InlineData(3f)]
        [InlineData(4.5f)]
        [InlineData(6f)]
        public void OlderRoundsKeepTheirPenetration(float penetration)
        {
            var (standoff, cone) = ShapedCharge.Fit(penetration, 4.5f);
            Assert.Equal(penetration, ShapedCharge.Penetration(standoff, cone, 4.5f), 3);
        }

        [Theory]
        [InlineData(1f, 1.3f)]
        [InlineData(10f, 6.37f)]
        public void OlderRoundsPastTheSlidersAreClamped(float penetration, float expected)
        {
            var (standoff, cone) = ShapedCharge.Fit(penetration, 4.5f);
            Assert.Equal(expected, ShapedCharge.Penetration(standoff, cone, 4.5f), 1);
        }
    }
}
