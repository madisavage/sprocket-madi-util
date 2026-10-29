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
    }
}
