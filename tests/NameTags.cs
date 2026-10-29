using StagedAmmo;
using Xunit;

namespace Tests
{
    /// <summary>Rack and drive name tags are saved in vehicle files: editing one tag must keep the rest of the name.</summary>
    public class NameTags
    {
        [Fact]
        public void RackTagsRoundTrip()
        {
            const string name = "Rack [auto x2] [clip 3]";

            string ready = StagedAmmo.Plugin.WithTag(name, StagedAmmo.Plugin.ReadyTag, "[ready]");
            Assert.True(StagedAmmo.Plugin.IsFlagged(ready));
            Assert.Equal(2f, StagedAmmo.Plugin.RackSpeed(ready));
            Assert.Equal(3, StagedAmmo.Plugin.ClipSize(ready));

            string unready = StagedAmmo.Plugin.WithTag(ready, StagedAmmo.Plugin.ReadyTag, null);
            Assert.False(StagedAmmo.Plugin.IsFlagged(unready));
            Assert.Equal(2f, StagedAmmo.Plugin.RackSpeed(unready));
            Assert.Equal(3, StagedAmmo.Plugin.ClipSize(unready));

            string faster = StagedAmmo.Plugin.WithTag(unready, 1.5f);
            Assert.Equal(1.5f, StagedAmmo.Plugin.RackSpeed(faster));
            Assert.Equal(3, StagedAmmo.Plugin.ClipSize(faster));
            Assert.StartsWith("Rack ", faster);

            string manual = StagedAmmo.Plugin.WithTag(faster, null);
            Assert.Null(StagedAmmo.Plugin.RackSpeed(manual));
            Assert.Equal("Rack [clip 3]", manual);
        }

        [Fact]
        public void RackSpeedDefaultsAndUntagged()
        {
            Assert.Equal("Rack [auto]", StagedAmmo.Plugin.WithTag("Rack", 1f));
            Assert.Equal(1f, StagedAmmo.Plugin.RackSpeed("Rack [AUTO]"));
            Assert.Null(StagedAmmo.Plugin.RackSpeed("Rack"));
            Assert.Equal(1, StagedAmmo.Plugin.ClipSize("Rack"));
        }

        [Fact]
        public void StabilizerTagRoundTrip()
        {
            string tagged = Stabilizer.Plugin.WithLimit("Turret ring [x]", 30f);
            Assert.Equal("Turret ring [x] [stab 30]", tagged);
            Assert.Equal(30f, Stabilizer.Plugin.Limit(tagged));

            string anySpeed = Stabilizer.Plugin.WithLimit(tagged, 0f);
            Assert.Equal(0f, Stabilizer.Plugin.Limit(anySpeed));

            string off = Stabilizer.Plugin.WithLimit(anySpeed, null);
            Assert.Equal("Turret ring [x]", off);
            Assert.Null(Stabilizer.Plugin.Limit(off));

            Assert.Equal("Gun [x]", Stabilizer.Plugin.WithLimit("Gun [stab 20] [x]", null));
        }
    }
}
