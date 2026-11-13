using System.Collections.Generic;
using System.Text.Json.Nodes;
using MadiUtil;
using Xunit;

namespace Tests
{
    /// <summary>Dropping parts a removed mod made from a vehicle file's JSON.</summary>
    public class MissingPartsStrip
    {
        private const string File = """
            {"v": "2.0", "objects": [
              {"guid": "hull", "vuid": 0, "pvuid": -1, "transform": {"mirrorVuid": -1}},
              {"guid": "modded", "vuid": 1, "pvuid": 0, "transform": {"mirrorVuid": 3}},
              {"guid": "gun", "vuid": 2, "pvuid": 1, "transform": {"mirrorVuid": -1}},
              {"guid": "gun", "vuid": 3, "pvuid": 0, "transform": {"mirrorVuid": 1}}
            ]}
            """;

        private static List<int> Vuids(string json)
        {
            var vuids = new List<int>();
            foreach (JsonNode? o in JsonNode.Parse(json)!["objects"]!.AsArray())
                vuids.Add((int)o!["vuid"]!);
            return vuids;
        }

        [Fact]
        public void DropsUnknownPartAndItsChildren()
        {
            var removed = new List<string>();
            string? result = MissingParts.Strip(File, g => g != "modded", removed);
            Assert.NotNull(result);
            Assert.Equal(new[] { 0, 3 }, Vuids(result!));
            Assert.Equal(new[] { "modded" }, removed);
            // The kept part mirrored the dropped one: its link is cleared
            Assert.Equal(-1, (int)JsonNode.Parse(result!)!["objects"]![1]!["transform"]!["mirrorVuid"]!);
        }

        [Fact]
        public void AllKnownIsUnchanged() => Assert.Null(MissingParts.Strip(File, _ => true, new()));

        [Fact]
        public void UnknownRootMeansTheLookupIsWrong() => Assert.Null(MissingParts.Strip(File, g => g != "hull", new()));

        [Fact]
        public void NotJsonIsLeftAlone() => Assert.Null(MissingParts.Strip("{\"objects\": [NaN]}", _ => false, new()));
    }
}
