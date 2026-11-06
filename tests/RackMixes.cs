using System.Text.Json.Nodes;
using Rounds;
using Xunit;

namespace Tests
{
    /// <summary>Mixed racks: reading the design, keeping counts in step with the game's, and picking what to re-rack.</summary>
    public class RackMixes
    {
        private static readonly string[] Ids = { "ap", "he", "heat" };

        private static RackMix Mix(string json, int capacity = 18) => RackMix.Read(JsonNode.Parse(json)!.AsObject(), Ids, capacity)!;

        [Fact]
        public void OldRackIsAllItsRound()
        {
            RackMix mix = Mix("""{"round": "he"}""");
            Assert.Equal(18, mix.Count("he"));
            Assert.Equal(0, mix.Count("ap"));
            Assert.Equal(0, mix.Unassigned);
        }

        [Fact]
        public void RackWithoutAPickHoldsTheFirstRound()
        {
            Assert.Equal(18, Mix("{}").Count("ap"));
            Assert.Equal(18, Mix("""{"round": "deleted"}""").Count("ap"));
        }

        [Fact]
        public void NoRoundsIsNoMix() => Assert.Null(RackMix.Read(new JsonObject(), new string[0], 18));

        [Fact]
        public void FullyAssignedSplitIsThoseCounts()
        {
            RackMix mix = Mix("""{"round": "ap", "split": {"ap": 15, "he": 3}}""");
            Assert.Equal(15, mix.Count("ap"));
            Assert.Equal(3, mix.Count("he"));
            Assert.Equal(0, mix.Count("heat"));
        }

        [Fact]
        public void UnassignedSlotsStartEvenlyBalanced()
        {
            RackMix mix = Mix("""{"split": {"he": 3}}""");
            Assert.Equal(15, mix.Unassigned);
            Assert.Equal(5, mix.Count("ap"));
            Assert.Equal(8, mix.Count("he"));
            Assert.Equal(5, mix.Count("heat"));
        }

        [Fact]
        public void EmptySplitIsAllUnassigned()
        {
            RackMix mix = Mix("""{"round": "he", "split": {}}""");
            Assert.Equal(6, mix.Count("ap"));
            Assert.Equal(6, mix.Count("he"));
            Assert.Equal(6, mix.Count("heat"));
        }

        [Fact]
        public void SplitIsCutToCapacity()
        {
            RackMix mix = Mix("""{"split": {"he": 3, "heat": 5}}""", 6);
            Assert.Equal(3, mix.Count("he"));
            Assert.Equal(3, mix.Count("heat"));
            Assert.Equal(0, mix.Count("ap"));
        }

        [Fact]
        public void BadSplitEntriesDropOut()
        {
            RackMix mix = Mix("""{"split": {"deleted": 3, "he": "many", "heat": -1, "ap": 18}}""");
            Assert.Equal(18, mix.Count("ap"));
            Assert.Equal(0, mix.Count("deleted"));
            Assert.Equal(0, mix.Count("he"));
            Assert.Equal(0, mix.Count("heat"));
        }

        [Fact]
        public void LimitIsItsOwnSlotsAndTheUnassigned()
        {
            RackMix mix = Mix("""{"split": {"he": 3, "heat": 5}}""");
            Assert.Equal(13, mix.Limit("he"));
            Assert.Equal(15, mix.Limit("heat"));
            Assert.Equal(10, mix.Limit("ap"));
        }

        [Fact]
        public void TakeComesOffTheRoundTaken()
        {
            RackMix mix = Mix("""{"split": {"ap": 15, "he": 3}}""");
            mix.Sync(17, "he");
            Assert.Equal(2, mix.Count("he"));
            Assert.Equal(15, mix.Count("ap"));
        }

        [Fact]
        public void UnexplainedDropComesOffUnassignedFirst()
        {
            RackMix mix = Mix("""{"split": {"he": 3}}""");
            mix.Sync(3);
            Assert.Equal(3, mix.Count("he"));
            Assert.Equal(0, mix.Count("ap"));
            Assert.Equal(0, mix.Count("heat"));
            mix.Sync(0);
            Assert.Equal(0, mix.Count("he"));
        }

        [Fact]
        public void ResupplyRefillsAssignedSlotsThenBalances()
        {
            RackMix mix = Mix("""{"split": {"he": 3}}""");
            mix.Sync(0);
            mix.Sync(2);
            Assert.Equal(2, mix.Count("he"));
            Assert.Equal(0, mix.Count("ap"));
            mix.Sync(18);
            Assert.Equal(5, mix.Count("ap"));
            Assert.Equal(8, mix.Count("he"));
            Assert.Equal(5, mix.Count("heat"));
        }

        [Fact]
        public void PutBackDoesNotBecomeARoundShortOfItsSlots()
        {
            RackMix mix = Mix("""{"split": {"ap": 15, "he": 3}}""");
            mix.Sync(17, "he");
            mix.Sync(16, "ap");
            mix.Sync(17, "ap");
            Assert.Equal(2, mix.Count("he"));
            Assert.Equal(15, mix.Count("ap"));
        }

        [Fact]
        public void RerackBringsTheNextRoundFirstWhenShort()
        {
            RackMix mix = Mix("""{"split": {"ap": 15, "he": 3}}""");
            mix.Sync(17, "he");
            mix.Sync(16, "ap");
            Assert.Equal(new[] { "ap", "he", "heat" }, mix.Wanted("ap"));
            Assert.Equal(new[] { "he", "ap", "heat" }, mix.Wanted("he"));
        }

        [Fact]
        public void RerackThenPicksTheRoundFurthestShort()
        {
            RackMix mix = Mix("""{"split": {"ap": 12, "he": 3, "heat": 3}}""");
            mix.Sync(17, "he");
            mix.Sync(16, "heat");
            mix.Sync(15, "heat");
            Assert.Equal(new[] { "heat", "he", "ap" }, mix.Wanted(null));
        }

        [Fact]
        public void RerackBalancesUnassignedSlotsWhateverIsNext()
        {
            RackMix mix = Mix("""{"split": {}}""");
            mix.Sync(17, "ap");
            mix.Sync(16, "he");
            mix.Sync(15, "he");
            Assert.Equal(new[] { "he", "ap", "heat" }, mix.Wanted("heat"));
        }

        [Fact]
        public void AssignedSlotsPreferTheirRoundThenTakeAnyNextFirst()
        {
            RackMix old = Mix("""{"round": "he"}""");
            old.Sync(17, "he");
            Assert.Equal(new[] { "he", "ap", "heat" }, old.Wanted("ap"));
            RackMix split = Mix("""{"split": {"ap": 15, "he": 3}}""");
            split.Sync(17, "he");
            Assert.Equal(new[] { "he", "ap", "heat" }, split.Wanted("ap"));
        }

        [Fact]
        public void FullRackWantsNothing() => Assert.Empty(Mix("""{"split": {"ap": 15, "he": 3}}""").Wanted("he"));
    }
}
