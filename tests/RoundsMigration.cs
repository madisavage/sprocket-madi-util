using System.Text.Json.Nodes;
using Rounds;
using Xunit;

namespace Tests
{
    /// <summary>Moving rounds an older version kept in the shell slot's description to the cannon's.</summary>
    public class RoundsMigration
    {
        [Fact]
        public void MovesRoundsAndKeepsShellsOtherKeys()
        {
            var moved = Loadout.Moved("{\"x\":1}", "{\"rounds\":{\"a\":{}},\"y\":2}");
            Assert.NotNull(moved);
            var owner = JsonNode.Parse(moved!.Value.Owner)!;
            Assert.Equal(1, (int)owner["x"]!);
            Assert.NotNull(owner["rounds"]!["a"]);
            Assert.Equal("{\"y\":2}", moved.Value.Shell);
        }

        [Fact]
        public void BlanksShellLeftEmpty() =>
            Assert.Equal(("{\"rounds\":{}}", ""), Loadout.Moved("", "{\"rounds\":{}}"));

        [Fact]
        public void LeavesOwnerWithRoundsAlone() =>
            Assert.Null(Loadout.Moved("{\"rounds\":{}}", "{\"rounds\":{\"a\":{}}}"));

        [Fact]
        public void LeavesNonJsonOwnerAlone() =>
            Assert.Null(Loadout.Moved("my notes", "{\"rounds\":{}}"));

        [Fact]
        public void NothingToMove() =>
            Assert.Null(Loadout.Moved("", "{\"y\":2}"));
    }
}
