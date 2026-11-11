extern alias UnityCoreModule;

using System.Collections.Generic;
using MadiLib;
using UnityEngine;
using Camera = UnityCoreModule::UnityEngine.Camera;
using Color = UnityCoreModule::UnityEngine.Color;
using Rect = UnityCoreModule::UnityEngine.Rect;
using Screen = UnityCoreModule::UnityEngine.Screen;
using Time = UnityCoreModule::UnityEngine.Time;
using UnityVector3 = UnityCoreModule::UnityEngine.Vector3;
using Vector3 = System.Numerics.Vector3;

namespace GuidedMunitions
{
    /// <summary>
    /// Shows the player's guided rounds: a HUD panel line per round (guided and
    /// how far along the wire, waiting for its motor, or why guidance ended) and
    /// a box around each on screen, green while guided, red once lost.
    /// </summary>
    internal static class Indicator
    {
        /// <summary>One of the player's guided rounds, as of its last guidance step.</summary>
        internal sealed class Shot
        {
            internal Vector3 Position;
            internal float Distance, Seen;
            internal bool Lit;
            internal string? Lost;
            internal float LostAt;
        }

        // Seconds a lost round's line and box stay up; a round not stepped for this long has hit or expired
        private const float Linger = 3f, Stale = 0.5f;

        private static readonly List<Shot> shots = new();

        /// <summary>Starts showing a new guided round of the player's.</summary>
        internal static Shot Track()
        {
            var shot = new Shot { Seen = Time.time };
            shots.Add(shot);
            return shot;
        }

        /// <summary>The HUD panel and on-screen boxes; a MadiLib <see cref="Hud"/> panel.</summary>
        internal static float Draw(float left, float bottom)
        {
            float now = Time.time;
            shots.RemoveAll(s => s.Lost != null ? now - s.LostAt > Linger : now - s.Seen > Stale);
            if (shots.Count == 0)
                return 0f;
            const float line = 24f, pad = 6f, width = 330f;
            Camera? camera = Camera.main;
            float height = shots.Count * line + 2 * pad;
            GUI.Box(new Rect(left, bottom - height, width, height), "");
            for (int i = 0; i < shots.Count; i++)
            {
                Shot shot = shots[i];
                var (text, color) = shot.Lost != null ? ($"Guidance lost: {shot.Lost}", HudDraw.Blocked)
                    : !shot.Lit ? ("Guided round: motor not lit", HudDraw.Next)
                    : ($"GUIDED  {shot.Distance / 1000f:0.0} km / {Plugin.WireLength.Value / 1000f:0.0} km wire", HudDraw.Ok);
                HudDraw.Label(new Rect(left + pad, bottom - height + pad + i * line, width - 2 * pad, line), text, 16, color, TextAnchor.MiddleLeft);
                if (camera != null)
                    Box(camera, shot.Position, color);
            }
            return height;
        }

        /// <summary>A box around <paramref name="position"/> on screen, sized so it stays visible far out; nothing if it's behind the camera.</summary>
        private static void Box(Camera camera, Vector3 position, Color color)
        {
            UnityVector3 at = camera.WorldToScreenPoint(new UnityVector3(position.X, position.Y, position.Z));
            if (at.z <= 0f)
                return;
            const float size = 22f;
            HudDraw.Outline(new Rect(at.x - size / 2f, Screen.height - at.y - size / 2f, size, size), color);
        }
    }
}
