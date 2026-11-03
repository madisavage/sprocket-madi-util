using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShellCam;
using Xunit;

namespace Tests
{
    /// <summary>Replay timing: each fragment starts when its parent ends.</summary>
    public class ShellCamTimeline
    {
        [Fact]
        public void ChildrenStartAtTheirParentsEnd()
        {
            // 0: the shell; 1 and 2: spall off it; 3: spall off 1
            var starts = Timeline.Starts(new short[] { -1, 0, 0, 1 }, new[] { 0.5f, 2f, 1f, 1f });
            Assert.Equal(new[] { 0f, 0.5f, 0.5f, 2.5f }, starts);
        }

        [Fact]
        public void BadParentsAreRoots()
        {
            // A parent after its child, or itself, would read a start not yet set
            var starts = Timeline.Starts(new short[] { 1, 1, -5 }, new[] { 1f, 1f, 1f });
            Assert.Equal(new[] { 0f, 0f, 0f }, starts);
        }
    }

    /// <summary>The X-ray image: each triangle adds one layer where it covers a pixel.</summary>
    public class ShellCamRaster
    {
        [Fact]
        public void SquareOfTwoTrianglesCoversEachPixelOnce()
        {
            // Diagonal y = x + 0.2 misses every pixel centre; the square holds all 16
            var cover = new byte[16];
            Vector2 a = new(0f, 0.2f), b = new(4f, 0.2f), c = new(4f, 4.2f), d = new(0f, 4.2f);
            Raster.Triangle(cover, 4, a, b, c);
            Raster.Triangle(cover, 4, a, c, d);
            Assert.All(cover, n => Assert.Equal(1, n));
        }

        [Fact]
        public void EitherWindingAndOffImageAreFine()
        {
            var cover = new byte[16];
            // Clockwise this time, and reaching far off the image
            Raster.Triangle(cover, 4, new Vector2(-10f, -10f), new Vector2(-10f, 30f), new Vector2(30f, -10f));
            Assert.True(cover.Count(n => n == 1) >= 15);
            Assert.All(cover, n => Assert.InRange(n, 0, 1));
        }

        [Fact]
        public void NaNCornerIsSkipped()
        {
            var cover = new byte[16];
            Raster.Triangle(cover, 4, new Vector2(float.NaN, 0f), new Vector2(4f, 0f), new Vector2(0f, 4f));
            Assert.All(cover, n => Assert.Equal(0, n));
        }
    }

    /// <summary>The replay frames the vehicle, ignoring stray points.</summary>
    public class ShellCamFraming
    {
        [Fact]
        public void StrayPointsDontZoomOut()
        {
            // A 4 m cube of points, and one 500 m off
            var points = new List<Vector3>();
            for (int i = 0; i < 1000; i++)
                points.Add(new Vector3(i % 10 * 0.4f, i / 10 % 10 * 0.4f, i / 100 * 0.4f));
            points.Add(new Vector3(500f, 0f, 0f));
            var (centre, radius) = Framing.Fit(points, new Vector3(1f, 1f, 1f));
            Assert.InRange(radius, 2f, 4f);
            Assert.InRange(centre.X, 1f, 3f);
        }

        [Fact]
        public void EntryStaysInViewWhenCapped()
        {
            var points = new List<Vector3> { new(-100f, 0f, 0f), new(100f, 0f, 0f) };
            var entry = new Vector3(90f, 0f, 0f);
            var (centre, radius) = Framing.Fit(points, entry);
            Assert.Equal(10f, radius);
            Assert.True((entry - centre).Length() <= radius);
        }
    }

    public class ShellCamShots
    {
        private static readonly List<(float Mass, float Diameter, int Projectile, float Time)> shots = new() { (10f, 0.1f, 7, 100f) };

        [Fact]
        public void TheShellFiredIsMine() => Assert.True(Hits.Mine(shots, 10f, 0.1f, 7, 101f));

        [Fact]
        public void AnotherGunsSameShellIsNot() => Assert.False(Hits.Mine(shots, 10f, 0.1f, 8, 101f));

        [Fact]
        public void AHitThatIsNoShellIsNot() => Assert.False(Hits.Mine(shots, 10f, 0.1f, -1, 101f));

        [Fact]
        public void AnIdUsedAgainLaterIsNot() => Assert.False(Hits.Mine(shots, 10f, 0.1f, 7, 200f));
    }

    public class ShellCamHealth
    {
        [Fact]
        public void TypeNamesReadAsWords() => Assert.Equal("Ammo rack", Health.Label("AmmoRack"));

        [Fact]
        public void TrianglesGoToTheirOwnObjectsPart()
        {
            // One mesh's triangles: objects 5 and 6 are parts, 7 shares 5's pool, 8 has no health and -1 is unmapped
            var pools = new Dictionary<int, System.IntPtr> { [5] = new(100), [6] = new(200), [7] = new(100) };
            var (owners, parts) = Health.Assign(new[] { 5, 8, 6, 5, 7, -1 }, o => pools.GetValueOrDefault(o));
            Assert.Equal(new[] { 0, -1, 1, 0, 0, -1 }, owners);
            Assert.Equal(new[] { new System.IntPtr(100), new System.IntPtr(200) }, parts);
        }
    }
}
