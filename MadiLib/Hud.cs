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
    /// Text panels in the bottom-left corner of the screen. Each panel sizes
    /// itself to its text; panels stack upward in the order they were added.
    /// </summary>
    public static class Hud
    {
        private const float Margin = 12f, Gap = 6f;

        private static readonly List<Func<string?>> panels = new();
        private static GUIStyle? style;

        /// <summary>Adds a panel.</summary>
        /// <param name="text">
        /// Called every GUI frame for the panel's text, one line per row.
        /// Return null or empty to hide the panel. A panel whose text throws
        /// is removed and the error logged.
        /// </param>
        public static void AddPanel(Func<string?> text) => panels.Add(text);

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
                string? text;
                try { text = panels[i](); }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"HUD panel removed after it threw: {e}");
                    panels.RemoveAt(i--);
                    continue;
                }
                if (string.IsNullOrEmpty(text))
                    continue;

                Vector2 size = style.CalcSize(new GUIContent(text));
                bottom -= size.y;
                GUI.Box(new Rect(Margin, bottom, size.x, size.y), text, style);
                bottom -= Gap;
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
