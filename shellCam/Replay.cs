extern alias UnityCoreModule;

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using MadiLib;
using UnityEngine;
using Color = UnityCoreModule::UnityEngine.Color;
using FilterMode = UnityCoreModule::UnityEngine.FilterMode;
using HideFlags = UnityCoreModule::UnityEngine.HideFlags;
using Mathf = UnityCoreModule::UnityEngine.Mathf;
using Rect = UnityCoreModule::UnityEngine.Rect;
using Screen = UnityCoreModule::UnityEngine.Screen;
using Texture2D = UnityCoreModule::UnityEngine.Texture2D;
using TextureFormat = UnityCoreModule::UnityEngine.TextureFormat;
using Time = UnityCoreModule::UnityEngine.Time;

namespace ShellCam
{
    /// <summary>
    /// Plays a recorded hit back in a panel in the bottom-right corner, over
    /// an X-ray view of the vehicle's armour (each plate adds light, so where
    /// plates overlap is brighter): the shell flies in, then its path through
    /// the armour and each spall fragment grow outward in the order they
    /// happened, slowed to <see cref="Plugin.ReplaySeconds"/>; the whole hit
    /// then stays a moment and fades. The view looks from the side of the
    /// shell's path and slowly circles the vehicle.
    /// </summary>
    /// <remarks>
    /// Drawn in software into a texture shown with a label style:
    /// <c>GUI.DrawTexture</c> is stripped from the game, and the hit's
    /// vehicle may be far off or destroyed, so no game camera is used.
    /// </remarks>
    internal static class Replay
    {
        internal const float Approach = 3f;
        private const float LeadIn = 0.5f, Hold = 2f, Fade = 1f, Margin = 12f, Pad = 8f, CaptionHeight = 20f;
        // How fast the view circles the vehicle, degrees per second; how often the image is drawn again, per second
        private const float Orbit = 12f, Rate = 30f;
        private const int ShellWidth = 3, SpallWidth = 2;

        private static readonly uint background = Pack(12, 16, 22, 235);
        private static readonly (byte R, byte G, byte B) armour = (150, 195, 240);
        private static readonly Color shell = HudDraw.Next, ricochet = new(0.4f, 0.85f, 1f), spall = HudDraw.Blocked;

        // Brightness by how many armour layers cover a pixel: light passing through more plates, the X-ray look
        private static readonly uint[] layers = new uint[64];

        // The hit playing, as an index into Hits.Recent, or -1
        private static int playing = -1;
        private static float started, drawn, noHits = float.NegativeInfinity;

        private static Texture2D? texture;
        private static int side;
        private static uint[] pixels = Array.Empty<uint>();
        private static byte[] cover = Array.Empty<byte>();
        // Which damaged part (index + 1) covers each pixel, 0 for none
        private static byte[] mark = Array.Empty<byte>();

        static Replay()
        {
            for (int n = 0; n < layers.Length; n++)
            {
                float k = 1f - MathF.Exp(-0.3f * n);
                layers[n] = Mix(background, armour.R, armour.G, armour.B, k);
            }
        }

        /// <summary>The keybind: replays the latest hit, or while one plays, the one before it.</summary>
        internal static void Next()
        {
            if (Hits.Recent.Count == 0)
            {
                noHits = Time.realtimeSinceStartup;
                return;
            }
            Start(playing >= 0 && playing + 1 < Hits.Recent.Count ? playing + 1 : 0);
        }

        internal static void Start(int index)
        {
            playing = index;
            started = Time.realtimeSinceStartup;
            drawn = float.NegativeInfinity;
        }

        /// <summary>A line saying there's nothing to replay yet, for a moment after the key is pressed; else null.</summary>
        internal static string? Caption() =>
            Time.realtimeSinceStartup - noHits < Hold ? "Shell cam: no hits recorded yet" : null;

        /// <summary>The view: the hit's centre at the image's middle, the screen's right and up in sim space, and pixels per metre.</summary>
        private readonly record struct View(Vector3 Centre, Vector3 Right, Vector3 Up, float Middle, float Scale)
        {
            public Vector2 Project(Vector3 p)
            {
                Vector3 d = p - Centre;
                return new Vector2(Middle + Vector3.Dot(d, Right) * Scale, Middle - Vector3.Dot(d, Up) * Scale);
            }
        }

        internal static void Draw()
        {
            if (playing < 0)
                return;
            if (playing >= Hits.Recent.Count)
            {
                playing = -1;
                return;
            }
            using var _ = MadiLib.Profile.Measure("shellCam: replay");
            Hit hit = Hits.Recent[playing];
            float play = Plugin.ReplaySeconds.Value;
            float now = Time.realtimeSinceStartup, t = now - started;
            if (t > LeadIn + play + Hold + Fade)
            {
                playing = -1;
                return;
            }

            float size = Plugin.PanelSize.Value;
            var panel = new Rect(Screen.width - Margin - size, Screen.height - Margin - size, size, size);
            var area = new Rect(panel.x + Pad, panel.y + Pad + CaptionHeight, size - 2 * Pad, size - 2 * Pad - CaptionHeight);
            int wanted = (int)Mathf.Min(area.width, area.height);
            View view = Look(hit, t, wanted);
            if (Texture(wanted) is not { } image)
                return;
            if (now - drawn >= 1f / Rate)
            {
                drawn = now;
                Render(hit, view, t, play, image);
            }

            float alpha = 1f - Mathf.Clamp01((t - LeadIn - play - Hold) / Fade);
            GUI.Box(panel, "");
            HudDraw.Label(new Rect(panel.x + Pad, panel.y + 4f, size - 2 * Pad, CaptionHeight),
                $"Hit {playing + 1} of {Hits.Recent.Count}: {hit.Outcome}", 13, HudDraw.Idle, TextAnchor.MiddleLeft);
            var picture = new Rect(area.x + (area.width - side) / 2f, area.y + (area.height - side) / 2f, side, side);
            HudDraw.Image(picture, image, new Color(1f, 1f, 1f, alpha));
            ScaleBar(picture, view.Scale);
            if (t >= LeadIn + play)
                Damage(hit, picture);
        }

        /// <summary>The image texture, made again when the panel's size changes; null if it can't be made.</summary>
        private static Texture2D? Texture(int size)
        {
            if (texture != null && !texture.WasCollected && side == size)
                return texture;
            side = size;
            pixels = new uint[size * size];
            cover = new byte[size * size];
            mark = new byte[size * size];
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            drawn = float.NegativeInfinity;
            return texture;
        }

        /// <summary>
        /// Looks level at the vehicle from the side of the shell's path, turned
        /// <see cref="Orbit"/> degrees a second since the replay began, scaled
        /// so the vehicle and hit fill an image <paramref name="size"/> pixels across.
        /// </summary>
        private static View Look(Hit hit, float t, int size)
        {
            Vector3 up = Vector3.UnitY;
            Vector3 across = Vector3.Cross(up, hit.Entry - hit.ApproachFrom);
            // A shell falling straight down has no side; look along forward then
            Vector3 forward = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.UnitZ;
            float yaw = Orbit * t * MathF.PI / 180f;
            forward = forward * MathF.Cos(yaw) + Vector3.Cross(up, forward) * MathF.Sin(yaw);
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
            return new View(hit.Centre, right, up, size / 2f, size / 2f / hit.Radius * 0.92f);
        }

        /// <summary>Draws the armour and the hit as far as it's got into <paramref name="image"/>.</summary>
        private static void Render(Hit hit, View view, float t, float play, Texture2D image)
        {
            using var _ = MadiLib.Profile.Measure("shellCam: render");
            Array.Clear(cover);
            var points = hit.Points;
            var screen = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
                screen[i] = view.Project(points[i]);
            var tris = hit.Triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                Raster.Triangle(cover, side, screen[tris[i]], screen[tris[i + 1]], screen[tris[i + 2]]);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = layers[Math.Min(cover[i], (byte)(layers.Length - 1))];
            // Once the fragments have done their work, show what they did
            if (t >= LeadIn + play)
                Tint(hit, screen);

            Segment(view, hit.ApproachFrom, Vector3.Lerp(hit.ApproachFrom, hit.Entry, Math.Clamp(t / LeadIn, 0f, 1f)), shell, ShellWidth);
            if (t >= LeadIn)
            {
                // Sim time shown: the hit's whole length stretched over the play time
                float shown = hit.End * Math.Clamp((t - LeadIn) / play, 0f, 1f);
                foreach (Line line in hit.Lines)
                {
                    if (shown < line.Start)
                        continue;
                    float grown = line.Duration > 0f ? Math.Clamp((shown - line.Start) / line.Duration, 0f, 1f) : 1f;
                    Color color = line.Shell ? (line.Deflected ? ricochet : shell)
                        : spall with { a = 0.35f + 0.65f * (hit.MaxSpeed > 0f ? Math.Clamp(line.Speed / hit.MaxSpeed, 0f, 1f) : 1f) };
                    Segment(view, line.From, Vector3.Lerp(line.From, line.To, grown), color, line.Shell ? ShellWidth : SpallWidth);
                }
            }

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                image.LoadRawTextureData(handle.AddrOfPinnedObject(), pixels.Length * 4);
                image.Apply(false);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>Colours each damaged part by the health it has left: strongly if this hit hurt it, faintly if it was hurt before.</summary>
        private static void Tint(Hit hit, Vector2[] screen)
        {
            Array.Clear(mark);
            int count = Math.Min(hit.Parts.Count, 254);
            var colours = new (byte R, byte G, byte B, float A)[count];
            bool any = false;
            for (int k = 0; k < count; k++)
            {
                Part part = hit.Parts[k];
                if (part.After is not { } now || now >= 0.999f && part.Before - now <= 0.001f)
                    continue;
                var (r, g, b) = HealthColour(now);
                colours[k] = (r, g, b, part.Before - now > 0.001f ? 0.75f : 0.3f);
                any = true;
            }
            if (!any)
                return;
            for (int t = 0; t < hit.Owners.Length; t++)
            {
                int k = hit.Owners[t], i = 3 * t;
                if (k >= 0 && k < count && colours[k].A > 0f)
                    Raster.Triangle(mark, side, screen[hit.Triangles[i]], screen[hit.Triangles[i + 1]], screen[hit.Triangles[i + 2]], (byte)(k + 1));
            }
            for (int i = 0; i < pixels.Length; i++)
                if (mark[i] != 0 && colours[mark[i] - 1] is var (r, g, b, a) && a > 0f)
                    pixels[i] = Mix(pixels[i], r, g, b, a);
        }

        /// <summary>Yellow at full health through orange to red at none.</summary>
        private static (byte R, byte G, byte B) HealthColour(float fraction) =>
            fraction <= 0f ? ((byte)220, (byte)20, (byte)20) : ((byte)245, (byte)(60 + 160 * Math.Clamp(fraction, 0f, 1f)), (byte)40);

        /// <summary>Lists the parts this hit hurt, worst first, in the image's top-left corner.</summary>
        private static void Damage(Hit hit, Rect picture)
        {
            var hurt = new System.Collections.Generic.List<(string Name, float Before, float Now)>();
            foreach (Part part in hit.Parts)
                if (part.After is { } now && part.Before - now > 0.001f)
                    hurt.Add((part.Name, part.Before, now));
            hurt.Sort((x, y) => x.Now.CompareTo(y.Now));
            for (int i = 0; i < Math.Min(hurt.Count, 8); i++)
            {
                var (name, before, now) = hurt[i];
                var (r, g, b) = HealthColour(now);
                string text = now <= 0f ? $"{name}: destroyed" : $"{name}: {before:P0} to {now:P0}";
                HudDraw.Label(new Rect(picture.x + 6f, picture.y + 6f + 16f * i, picture.width - 12f, 16f), text, 12, new Color(r / 255f, g / 255f, b / 255f), TextAnchor.MiddleLeft);
            }
            if (hurt.Count > 8)
                HudDraw.Label(new Rect(picture.x + 6f, picture.y + 6f + 16f * 8, picture.width - 12f, 16f), $"and {hurt.Count - 8} more", 12, HudDraw.Idle, TextAnchor.MiddleLeft);
        }

        private static void Segment(View view, Vector3 from, Vector3 to, Color color, int width)
        {
            Vector2 a = view.Project(from), b = view.Project(to);
            float steps = MathF.Max(MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)), 1f);
            // Lines far outside the image would take forever to step along; NaN fails this too
            if (!(steps <= 4 * side))
                return;
            byte r = (byte)(color.r * 255f), g = (byte)(color.g * 255f), bl = (byte)(color.b * 255f);
            for (int i = 0; i <= (int)steps; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / steps);
                int cx = (int)p.X - width / 2, cy = (int)p.Y - width / 2;
                for (int y = cy; y < cy + width; y++)
                    for (int x = cx; x < cx + width; x++)
                        if ((uint)x < side && (uint)y < side)
                        {
                            int at = (side - 1 - y) * side + x;
                            pixels[at] = Mix(pixels[at], r, g, bl, color.a);
                        }
            }
        }

        /// <summary>A bar a round length long in the bottom-left of <paramref name="area"/>, for size.</summary>
        private static void ScaleBar(Rect area, float scale)
        {
            // The longest of these that fits in a quarter of the image
            float metres = 0.1f;
            foreach (float m in new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f })
                if (m * scale <= area.width / 4f)
                    metres = m;
            float length = metres * scale;
            HudDraw.Fill(new Rect(area.x + 6f, area.yMax - 8f, length, 2f), HudDraw.Idle);
            HudDraw.Label(new Rect(area.x + 6f, area.yMax - 24f, length + 40f, 14f), metres < 1f ? $"{metres * 100f:0} cm" : $"{metres:0.#} m", 11, HudDraw.Idle, TextAnchor.MiddleLeft);
        }

        // RGBA32 bytes in memory order, as a little-endian uint
        private static uint Pack(byte r, byte g, byte b, byte a) => r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

        /// <summary><paramref name="under"/> blended toward the colour by <paramref name="k"/> (0-1), keeping its alpha.</summary>
        private static uint Mix(uint under, byte r, byte g, byte b, float k)
        {
            byte Blend(int shift, byte over) => (byte)((under >> shift & 0xFF) + (over - (int)(under >> shift & 0xFF)) * k);
            return Pack(Blend(0, r), Blend(8, g), Blend(16, b), (byte)(under >> 24));
        }
    }

    /// <summary>Software rasterizing for the replay image, on a square of <c>side</c> pixels, rows stored bottom first as textures are.</summary>
    internal static class Raster
    {
        /// <summary>Adds a layer of cover to every pixel whose centre is strictly inside the triangle, so triangles sharing an edge never both count a pixel.</summary>
        /// <param name="set">Nonzero to write this value instead of adding a layer.</param>
        internal static void Triangle(byte[] cover, int side, Vector2 a, Vector2 b, Vector2 c, byte set = 0)
        {
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            // NaN area (a non-finite corner) fails this too; MathF.Sign would throw on it
            if (!(MathF.Abs(area) >= 1e-3f) || !float.IsFinite(area))
                return;
            int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0), x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), side - 1);
            int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0), y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), side - 1);
            float sign = MathF.Sign(area);
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                int row = (side - 1 - y) * side;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    if (sign * ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) <= 0f
                        || sign * ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) <= 0f
                        || sign * ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) <= 0f)
                        continue;
                    if (set != 0)
                        cover[row + x] = set;
                    else if (cover[row + x] < 255)
                        cover[row + x]++;
                }
            }
        }
    }
}
