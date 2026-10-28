extern alias UnityCoreModule;

using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Color = UnityCoreModule::UnityEngine.Color;
using HideFlags = UnityCoreModule::UnityEngine.HideFlags;
using Rect = UnityCoreModule::UnityEngine.Rect;
using RectOffset = UnityCoreModule::UnityEngine.RectOffset;
using Texture2D = UnityCoreModule::UnityEngine.Texture2D;

namespace Rounds
{
    /// <summary>
    /// The HUD's ammo bar, one row per player gun: a cell per round with its
    /// type's icon, its name and its shells (ready / all, when racks are
    /// staged). The next round is framed, yellow, or red while it waits for
    /// one in reach; the loaded round has a green bar; rounds with none left
    /// are dimmed. With more than one gun, the gun the round keys pick for is
    /// framed white, its cells numbered by key.
    /// </summary>
    internal static class AmmoBar
    {
        private const float CellWidth = 64f, CellHeight = 108f, Pad = 4f, NameHeight = 16f, CountHeight = 22f, Frame = 2f;
        private const int CountFont = 16;

        private static readonly Color next = new(1f, 0.82f, 0.2f), waiting = new(0.95f, 0.25f, 0.2f), loaded = new(0.3f, 0.9f, 0.35f), active = new(1f, 1f, 1f, 0.8f);

        // Icon key, its icon; null when there's none or its PNG didn't load
        private static readonly Dictionary<string, Texture2D?> icons = new();
        private static GUIStyle? name, count, label, image, fill, key;

        /// <summary>Draws a row per gun, bottom gun lowest, and returns their height.</summary>
        internal static float Draw(float left, float bottom)
        {
            using var _ = MadiLib.Profile.Measure("rounds: ammo bar");
            if (!Plugin.ShowIndicator.Value)
                return 0f;
            var guns = Tracker.Guns();
            if (guns.Count == 0)
                return 0f;
            // Built here, not at load: GUI.skin only exists inside OnGUI
            name ??= new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            key ??= new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, padding = new RectOffset(0, 0, 0, 0) };
            count ??= new GUIStyle(GUI.skin.label) { fontSize = CountFont, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
            label ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            // GUI.DrawTexture is stripped from the game, so images go through styles: a label scales its image down to fit
            image ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, imagePosition = ImagePosition.ImageOnly, padding = new RectOffset(0, 0, 0, 0) };
            if (fill == null)
            {
                fill = new GUIStyle();
                fill.normal.background = Texture2D.whiteTexture;
            }

            float top = bottom;
            for (int g = guns.Count - 1; g >= 0; g--)
            {
                var (loadedId, nextId, isWaiting, isActive, rounds) = guns[g];
                // With one gun, the keys always pick for it, so there's nothing to mark
                bool marked = isActive && guns.Count > 1;
                top -= CellHeight + 2 * Pad;
                var row = new Rect(left, top, rounds.Count * (CellWidth + Pad) + Pad, CellHeight + 2 * Pad);
                GUI.Box(row, "");
                if (marked)
                    Outline(row, active);
                for (int r = 0; r < rounds.Count; r++)
                {
                    var cell = new Rect(left + Pad + r * (CellWidth + Pad), top + Pad, CellWidth, CellHeight);
                    Cell(cell, rounds[r], rounds[r].Id == loadedId, rounds[r].Id == nextId, isWaiting);
                    if (marked && r < 9)
                        GUI.Label(new Rect(cell.x + Frame + 1f, cell.y + NameHeight, 12f, NameHeight), $"{r + 1}", key);
                }
                if (g > 0)
                    top -= Pad;
            }
            return bottom - top;
        }

        private static void Cell(Rect cell, (string Id, string Name, string Type, string Icon, int Left, int Ready) round, bool isLoaded, bool isNext, bool isWaiting)
        {
            Color before = GUI.color;
            if (isNext)
                Outline(cell, isWaiting ? waiting : next);

            GUI.color = round.Left == 0 ? new Color(1f, 1f, 1f, 0.35f) : before;
            GUI.Label(new Rect(cell.x, cell.y, cell.width, NameHeight), round.Name, name);
            var art = new Rect(cell.x + Pad, cell.y + NameHeight, cell.width - 2 * Pad, cell.height - NameHeight - CountHeight);
            if (Icon(round.Icon) is { } icon)
                GUI.Label(art, new GUIContent { image = icon }, image);
            else
                GUI.Label(art, Loadout.Types.FirstOrDefault(t => t.Type == round.Type).Label ?? round.Type, label);
            string shells = Loadout.IsReady != null ? $"{round.Ready}/{round.Left}" : $"{round.Left}";
            count!.fontSize = System.Math.Clamp((int)(cell.width / (0.62f * shells.Length)), 8, CountFont); // 0.62 roughly the width of a bold digit
            GUI.Label(new Rect(cell.x, cell.yMax - CountHeight, cell.width, CountHeight), shells, count);
            GUI.color = before;

            if (isLoaded)
                Fill(new Rect(cell.x + Pad, cell.yMax - CountHeight - 3f, cell.width - 2 * Pad, 3f), loaded);
        }

        private static void Outline(Rect r, Color color)
        {
            Fill(new Rect(r.x, r.y, r.width, Frame), color);
            Fill(new Rect(r.x, r.yMax - Frame, r.width, Frame), color);
            Fill(new Rect(r.x, r.y, Frame, r.height), color);
            Fill(new Rect(r.xMax - Frame, r.y, Frame, r.height), color);
        }

        private static void Fill(Rect r, Color color)
        {
            Color before = GUI.color;
            GUI.color = color;
            GUI.Box(r, GUIContent.none, fill);
            GUI.color = before;
        }

        /// <summary>The icon under <paramref name="key"/> (see <see cref="Loadout.IconOf"/>), loaded from its PNG on first use; null if there's none.</summary>
        private static Texture2D? Icon(string key)
        {
            if (icons.TryGetValue(key, out Texture2D? icon) && (icon == null || icon.WasCollected == false))
                return icon;
            icon = null;
            if (Loadout.Icons.TryGetValue(key, out byte[]? png))
            {
                icon = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                if (!ImageConversion.LoadImage(icon, new Il2CppStructArray<byte>(png)))
                {
                    Plugin.Logger.LogWarning($"Couldn't read the {key} icon; showing the round's type");
                    icon = null;
                }
            }
            return icons[key] = icon;
        }
    }
}
