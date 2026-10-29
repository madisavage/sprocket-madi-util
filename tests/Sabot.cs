using Subcaliber;
using Xunit;

namespace Tests
{
    /// <summary>A full-bore steel 3-caliber sabot round must fire exactly like the game's own shell.</summary>
    public class Sabot
    {
        [Fact]
        public void FullBoreSteelIsUnchanged()
        {
            var p = new Penetrator(1f, 3f, 1f, 0f, 1f);
            Assert.Equal(1f, p.Mass);
            Assert.Equal(1f, p.Launch);
            Assert.Equal(1f, p.Length);
        }

        [Fact]
        public void FullBorePenetrationIsDeMarre()
        {
            // 1250 m/s: above steel's Lanz-Odermatt minimum speed, where the old blend changed it
            Assert.Equal(Rounds.Impacts.DeMarre(105f, 20f, 1250f, 1800f), LongRod.Penetration(1f, 3f, 105f, 20f, "Steel", 1250f, 1800f));
        }

        [Fact]
        public void LengthFactorInterpolates()
        {
            Assert.Equal(1.35f, LongRod.Factor(2f));
            Assert.Equal(1.1f, LongRod.Factor(6f), 5);
            Assert.Equal(0.95f, LongRod.Factor(30f));
        }
    }
}
