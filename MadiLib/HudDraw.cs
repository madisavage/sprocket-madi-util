extern alias UnityCoreModule;

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Color = UnityCoreModule::UnityEngine.Color;
using HideFlags = UnityCoreModule::UnityEngine.HideFlags;
using Mathf = UnityCoreModule::UnityEngine.Mathf;
using Rect = UnityCoreModule::UnityEngine.Rect;
using RectOffset = UnityCoreModule::UnityEngine.RectOffset;
using Texture2D = UnityCoreModule::UnityEngine.Texture2D;

namespace MadiLib
{
    /// <summary>
    /// Drawing helpers for <see cref="Hud"/> panels, so every plugin's panel
    /// looks the same. Call only from inside a panel's draw (IMGUI).
    /// <c>GUI.DrawTexture</c> is stripped from the game, so fills and images
    /// go through label styles.
    /// </summary>
    public static class HudDraw
    {
        /// <summary>Width of <see cref="Outline"/>'s frame, in pixels.</summary>
        public const float Frame = 2f;

        /// <summary>Running, on, loaded.</summary>
        public static readonly Color Ok = new(0.3f, 0.9f, 0.35f);
        /// <summary>Next up, or waiting a moment (paused, over a limit).</summary>
        public static readonly Color Next = new(1f, 0.82f, 0.2f);
        /// <summary>Blocked: can't go on until something changes.</summary>
        public static readonly Color Blocked = new(0.95f, 0.25f, 0.2f);
        /// <summary>Switched off by the player.</summary>
        public static readonly Color Off = new(0.55f, 0.55f, 0.55f);
        /// <summary>Idle: nothing to do.</summary>
        public static readonly Color Idle = new(1f, 1f, 1f, 0.8f);

        // Icon key, its icon; null when its PNG didn't load
        private static readonly Dictionary<string, Texture2D?> icons = new();
        private static readonly Dictionary<(int, TextAnchor), GUIStyle> labels = new();
        private static GUIStyle? fill, image;

        /// <summary>Fills <paramref name="r"/> with <paramref name="color"/>.</summary>
        public static void Fill(Rect r, Color color)
        {
            if (fill == null)
            {
                fill = new GUIStyle();
                fill.normal.background = Texture2D.whiteTexture;
            }
            Color before = GUI.color;
            GUI.color = color;
            GUI.Box(r, GUIContent.none, fill);
            GUI.color = before;
        }

        /// <summary>Draws a frame <see cref="Frame"/> wide just inside <paramref name="r"/>.</summary>
        public static void Outline(Rect r, Color color)
        {
            Fill(new Rect(r.x, r.y, r.width, Frame), color);
            Fill(new Rect(r.x, r.yMax - Frame, r.width, Frame), color);
            Fill(new Rect(r.x, r.y, Frame, r.height), color);
            Fill(new Rect(r.xMax - Frame, r.y, Frame, r.height), color);
        }

        /// <summary>A bar filled from the left to <paramref name="fraction"/> (0-1) over a dark track.</summary>
        public static void Bar(Rect r, float fraction, Color color)
        {
            Fill(r, new Color(0f, 0f, 0f, 0.5f));
            Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fraction), r.height), color);
        }

        /// <summary>
        /// A bar centred on zero with a marker at <paramref name="value"/> out
        /// of ±<paramref name="limit"/>, filled from the centre to it.
        /// </summary>
        public static void CentredBar(Rect r, float value, float limit, Color color)
        {
            Fill(r, new Color(0f, 0f, 0f, 0.5f));
            float centre = r.x + r.width / 2f;
            float at = limit > 0f ? centre + r.width / 2f * Mathf.Clamp(value / limit, -1f, 1f) : centre;
            Fill(new Rect(Mathf.Min(centre, at), r.y, Mathf.Abs(at - centre), r.height), color);
            Fill(new Rect(centre - 0.5f, r.y - 2f, 1f, r.height + 4f), Idle);
        }

        /// <summary>Bold text in <paramref name="r"/>, tinted by <paramref name="color"/>.</summary>
        public static void Label(Rect r, string text, int fontSize, Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            if (!labels.TryGetValue((fontSize, anchor), out GUIStyle? style))
                labels[(fontSize, anchor)] = style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize, fontStyle = FontStyle.Bold, alignment = anchor, wordWrap = false,
                    clipping = TextClipping.Overflow, padding = new RectOffset(0, 0, 0, 0)
                };
            Color before = GUI.color;
            GUI.color = color;
            GUI.Label(r, text, style);
            GUI.color = before;
        }

        /// <summary>Draws <paramref name="icon"/> scaled down to fit <paramref name="r"/>, tinted by <paramref name="color"/>.</summary>
        public static void Image(Rect r, Texture2D icon, Color color)
        {
            image ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, imagePosition = ImagePosition.ImageOnly, padding = new RectOffset(0, 0, 0, 0) };
            Color before = GUI.color;
            GUI.color = color;
            GUI.Label(r, new GUIContent { image = icon }, image);
            GUI.color = before;
        }

        /// <summary>
        /// The icon from embedded resource <paramref name="resource"/> (a PNG)
        /// of <paramref name="assembly"/>, loaded on first use; null if it's
        /// missing or doesn't load.
        /// </summary>
        public static Texture2D? Icon(Assembly assembly, string resource)
        {
            string key = assembly.GetName().Name + "/" + resource;
            if (Cached(key, out Texture2D? icon))
                return icon;
            using Stream? png = assembly.GetManifestResourceStream(resource);
            if (png == null)
            {
                Plugin.Logger.LogWarning($"No HUD icon {key}");
                return icons[key] = null;
            }
            using var bytes = new MemoryStream();
            png.CopyTo(bytes);
            return Icon(key, bytes.ToArray());
        }

        /// <summary>The icon under <paramref name="key"/>, loaded from <paramref name="png"/> on first use; null if it doesn't load.</summary>
        public static Texture2D? Icon(string key, byte[] png)
        {
            if (Cached(key, out Texture2D? icon))
                return icon;
            icon = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(icon, new Il2CppStructArray<byte>(png)))
            {
                Plugin.Logger.LogWarning($"Couldn't read HUD icon {key}");
                icon = null;
            }
            return icons[key] = icon;
        }

        // A texture the game collected (scene change) is loaded again
        private static bool Cached(string key, out Texture2D? icon) =>
            icons.TryGetValue(key, out icon) && (icon == null || !icon.WasCollected);
    }
}
