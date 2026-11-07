using System;
using System.Linq;
using MadiUtil;
using Xunit;

namespace Tests
{
    /// <summary>The era dropdown's year rows: 1 January of each year, the era's start in its first.</summary>
    public class EraYearRows
    {
        [Fact]
        public void EraStartThenJanuaryFirsts()
        {
            var dates = EraYears.YearStarts(new DateOnly(1939, 9, 2), new DateOnly(1941, 12, 31), DateOnly.MaxValue).ToList();
            Assert.Equal(new[] { new DateOnly(1939, 9, 2), new DateOnly(1940, 1, 1), new DateOnly(1941, 1, 1) }, dates);
        }

        [Fact]
        public void StopsAtLimit()
        {
            var dates = EraYears.YearStarts(new DateOnly(1942, 1, 1), new DateOnly(1943, 12, 31), new DateOnly(1942, 6, 1)).ToList();
            Assert.Equal(new[] { new DateOnly(1942, 1, 1) }, dates);
        }

        [Fact]
        public void LatestTechSkipsBadFiles()
        {
            string dir = System.IO.Directory.CreateTempSubdirectory().FullName;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "a.json"), "{\"date\": \"1944.06.05\",}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "b.json"), "{\"date\": \"2031.01.01\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "c.json"), "not json");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "d.json.bak"), "{\"date\": \"9999.01.01\"}");
            Assert.Equal(new DateOnly(2031, 1, 1), EraYears.LatestTech(dir));
            System.IO.Directory.Delete(dir, true);
        }

        [Fact]
        public void LongEraIsCapped()
        {
            Assert.Equal(60, EraYears.YearStarts(new DateOnly(2025, 1, 1), new DateOnly(9998, 12, 31), DateOnly.MaxValue).Count());
        }
    }
}
