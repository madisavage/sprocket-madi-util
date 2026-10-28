extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using UnityEngine;
using MonoBehaviour = UnityCoreModule::UnityEngine.MonoBehaviour;
using Rect = UnityCoreModule::UnityEngine.Rect;
using RectOffset = UnityCoreModule::UnityEngine.RectOffset;
using Screen = UnityCoreModule::UnityEngine.Screen;
using Vector2 = UnityCoreModule::UnityEngine.Vector2;

namespace MadiLib
{
    /// <summary>
    /// Panels in the bottom-left corner of the screen, stacked upward in the
    /// order they were added. A text panel sizes itself to its text; a drawn
    /// panel draws itself.
    /// </summary>
    public static class Hud
    {
        private const float Margin = 12f, Gap = 6f;

        // Each panel: draws itself with its bottom edge at the given y, and returns its height (0 when hidden)
        private static readonly List<Func<float, float>> panels = new();
        private static GUIStyle? style;

        /// <summary>Adds a panel.</summary>
        /// <param name="text">
        /// Called every GUI frame for the panel's text, one line per row.
        /// Return null or empty to hide the panel. A panel whose text throws
        /// is removed and the error logged.
        /// </param>
        public static void AddPanel(Func<string?> text) => panels.Add(bottom =>
        {
            string? shown = text();
            if (string.IsNullOrEmpty(shown))
                return 0f;
            Vector2 size = style!.CalcSize(new GUIContent(shown));
            GUI.Box(new Rect(Margin, bottom - size.y, size.x, size.y), shown, style);
            return size.y;
        });

        /// <summary>Adds a panel that draws itself with IMGUI, inside <c>OnGUI</c>.</summary>
        /// <param name="draw">
        /// Called every GUI frame with the panel's left edge and bottom edge,
        /// in screen pixels; draws upward from there and returns its height,
        /// or 0 to hide. A panel that throws is removed and the error logged.
        /// </param>
        public static void AddPanel(Func<float, float, float> draw) => panels.Add(bottom => draw(Margin, bottom));

        /// <summary>Draws every visible panel. Called from <see cref="HudDrawer"/>.</summary>
        internal static void Draw()
        {
            if (panels.Count == 0)
                return;
            // Built here, not at load: GUI.skin only exists inside OnGUI
            style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 6, 6) };

            float bottom = Screen.height - Margin;
            for (int i = 0; i < panels.Count; i++)
            {
                float height;
                try { height = panels[i](bottom); }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"HUD panel removed after it threw: {e}");
                    panels.RemoveAt(i--);
                    continue;
                }
                if (height > 0f)
                    bottom -= height + Gap;
            }
        }
    }

    /// <summary>Component added to the plugin's object to call <see cref="Hud.Draw"/> every GUI frame.</summary>
    internal sealed class HudDrawer : MonoBehaviour
    {
        /// <summary>Wraps an existing native instance; used by Il2CppInterop.</summary>
        public HudDrawer(IntPtr ptr) : base(ptr) { }

        private void OnGUI() => Hud.Draw();
    }
}
