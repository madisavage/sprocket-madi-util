using MadiUtil;
using Xunit;

namespace Tests
{
    /// <summary>A cannon's breech settings in its description.</summary>
    public class BreechTests
    {
        [Fact]
        public void SettingsSitBesideRoundsAndPressure()
        {
            var settings = new Breech.Settings(0.5f, 0.3f, 0.9f, 0.25f, true);
            string set = MadiLib.Descriptions.With("{\"rounds\":{},\"pressure\":30000}", "breech", Breech.ToJson(settings))!;
            Assert.Equal(settings, Breech.Parse(set));
            Assert.Equal(30000u, Pressure.Parse(set));
            string back = MadiLib.Descriptions.With(set, "breech", Breech.ToJson(Breech.Default))!;
            Assert.Equal(Breech.Default, Breech.Parse(back));
            Assert.DoesNotContain("breech", back);
            Assert.Contains("\"rounds\"", back);
            Assert.Equal(Breech.Default, Breech.Parse(""));
            Assert.Equal(Breech.Default, Breech.Parse("my notes"));
        }

        [Fact]
        public void MissingPartsAreTheGamesAndValuesClamp()
        {
            Assert.Equal(Breech.Default with { X = 0.2f }, Breech.Parse("{\"breech\":{\"scale\":[0.2]}}"));
            Assert.Equal(Breech.Default with { X = Breech.MinScale, Recoil = 0f }, Breech.Parse("{\"breech\":{\"scale\":[0],\"recoil\":-1}}"));
        }

        [Fact]
        public void ReadsTheEarlierRecoillessKey()
        {
            Assert.Equal(new Breech.Settings(0.4f, 0.4f, 1f, 0f, true), Breech.Parse("{\"recoilless\":true}"));
            Assert.Equal(new Breech.Settings(0.5f, 0.3f, 0.9f, 0f, false), Breech.Parse("{\"recoilless\":{\"breech\":[0.5,0.3,0.9],\"backblast\":false}}"));
            Assert.Equal(Breech.Default, Breech.Parse("{\"recoilless\":false}"));
        }
    }
}
