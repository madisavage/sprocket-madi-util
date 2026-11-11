using System;
using System.IO;
using MadiUtil;
using Xunit;

namespace Tests
{
    /// <summary>The cannon panel's pressure slider: each era's pressure from the tech files.</summary>
    public class PressureTests
    {
        [Fact]
        public void ReadsCannonTechOnlyOldestFirst()
        {
            string dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "Late.json"), "{\"type\": \"cannon\", \"date\": \"1979.01.01\", \"properties\": {\"operatingPressure\": 97000, \"calibre\": 110,}}");
                File.WriteAllText(Path.Combine(dir, "Early.json"), "{\"type\": \"cannon\", \"date\": \"1946.01.01\", \"properties\": {\"operatingPressure\": 50000}}");
                File.WriteAllText(Path.Combine(dir, "Engine.json"), "{\"type\": \"engine\", \"date\": \"1950.01.01\", \"properties\": {\"operatingPressure\": 1}}");
                File.WriteAllText(Path.Combine(dir, "Broken.json"), "{");
                var eras = Pressure.Read(dir);
                Assert.Equal(new[] { (new DateOnly(1946, 1, 1), 50000u), (new DateOnly(1979, 1, 1), 97000u) }, eras);
                Assert.Equal(50000u, Pressure.Of(eras, new DateOnly(1967, 1, 1)));
                Assert.Equal(97000u, Pressure.Of(eras, new DateOnly(1979, 1, 1)));
                Assert.Equal(50000u, Pressure.Of(eras, new DateOnly(1900, 1, 1)));
                Assert.Null(Pressure.Of(Array.Empty<(DateOnly, uint)>(), new DateOnly(1967, 1, 1)));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void KeepsPressureBesideRounds()
        {
            string text = Pressure.With("{\"rounds\":{\"a\":{\"type\":\"ap\"}}}", 30000)!;
            Assert.Contains("\"rounds\"", text);
            Assert.Equal(30000u, Pressure.Parse(text));
            Assert.Equal(25000u, Pressure.Parse(Pressure.With(text, 25000)!));
            Assert.Equal(30000u, Pressure.Parse(Pressure.With("", 30000)!));
            Assert.Null(Pressure.With("my notes", 30000));
            Assert.Null(Pressure.Parse("my notes"));
            Assert.Null(Pressure.Parse("{}"));
        }
    }
}
