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

            string ready = MadiLib.NameTags.With(name, "ready", "[ready]");
            Assert.True(StagedAmmo.Plugin.IsFlagged(ready));
            Assert.Equal(2f, StagedAmmo.Plugin.RackSpeed(ready));
            Assert.Equal(3, StagedAmmo.Plugin.ClipSize(ready));

            string unready = MadiLib.NameTags.With(ready, "ready", null);
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

        [Fact]
        public void MalformedTagsCountAsNoneAndWritingClearsThem()
        {
            // The first [word ...] decides: malformed is no tag, in every plugin
            Assert.Null(Stabilizer.Plugin.Limit("Gun [stab abc] [stab 20]"));
            Assert.Null(StagedAmmo.Plugin.RackSpeed("Rack [auto fast] [auto x2]"));
            Assert.Equal(1, StagedAmmo.Plugin.ClipSize("Rack [clip] [clip 5]"));
            Assert.False(StagedAmmo.Plugin.IsFlagged("Rack [ready now] [ready]"));
            Assert.Null(Hydropneumatic.Plugin.Settings("Track [adjust x] [adjust 100]"));

            // Writing a tag clears the malformed one, so it reads back
            string fixedName = Stabilizer.Plugin.WithLimit("Gun [stab abc] [x]", 30f);
            Assert.Equal("Gun [x] [stab 30]", fixedName);
            Assert.Equal(30f, Stabilizer.Plugin.Limit(fixedName));
            Assert.Equal(2f, StagedAmmo.Plugin.RackSpeed(StagedAmmo.Plugin.WithTag("Rack [auto fast]", 2f)));

            // Words are whole: [autoloader] isn't [auto]
            Assert.Null(StagedAmmo.Plugin.RackSpeed("Rack [autoloader]"));
            Assert.Equal("Rack [autoloader] [auto]", StagedAmmo.Plugin.WithTag("Rack [autoloader]", 1f));
        }
    }
}
