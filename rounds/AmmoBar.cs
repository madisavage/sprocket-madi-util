extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using MadiLib;
using UnityEngine;
using Color = UnityCoreModule::UnityEngine.Color;
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
    /// framed white, its cells numbered by key. A plugin staging ammo adds a
    /// Each row is headed by its gun's name. A plugin staging ammo adds a
    /// re-rack progress bar under the round it moves (<see cref="Loadout.Reracking"/>)
    /// and a status column at the row's end (<see cref="Loadout.GunBadge"/>).
    /// </summary>
    internal static class AmmoBar
    {
        private const float CellWidth = 64f, CellHeight = 108f, Pad = 4f, NameHeight = 16f, CountHeight = 22f, BadgeWidth = 40f, GunHeight = 18f;
        private const int CountFont = 16;

        /// <summary>The guns drawn last GUI frame, for <see cref="Loadout.OnBar"/>.</summary>
        internal static readonly HashSet<IntPtr> Shown = new();

        private static GUIStyle? name, count, label, key;

        /// <summary>Draws a row per gun, bottom gun lowest, and returns their height.</summary>
        internal static float Draw(float left, float bottom)
        {
            using var _ = MadiLib.Profile.Measure("rounds: ammo bar");
            Shown.Clear();
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

            var badge = Loadout.GunBadge;
            float badgeWidth = badge != null ? BadgeWidth + Pad : 0f;
            float top = bottom;
            for (int g = guns.Count - 1; g >= 0; g--)
            {
                var (gun, gunName, loadedId, nextId, isWaiting, isActive, rounds) = guns[g];
                Shown.Add(gun);
                var rerack = Loadout.Reracking?.Invoke(gun);
                // With one gun, the keys always pick for it, so there's nothing to mark
                bool marked = isActive && guns.Count > 1;
                top -= GunHeight + CellHeight + 2 * Pad;
                var row = new Rect(left, top, rounds.Count * (CellWidth + Pad) + Pad + badgeWidth, GunHeight + CellHeight + 2 * Pad);
                GUI.Box(row, "");
                if (marked)
                    HudDraw.Outline(row, HudDraw.Idle);
                HudDraw.Label(new Rect(row.x + 2 * Pad, top + Pad, row.width - 4 * Pad, GunHeight), gunName, 13, marked ? HudDraw.Idle : new Color(1f, 1f, 1f, 0.6f), TextAnchor.MiddleLeft);
                float cells = top + Pad + GunHeight;
                for (int r = 0; r < rounds.Count; r++)
                {
                    var cell = new Rect(left + Pad + r * (CellWidth + Pad), cells, CellWidth, CellHeight);
                    Cell(cell, rounds[r], rounds[r].Id == loadedId, rounds[r].Id == nextId, isWaiting);
                    if (rerack is { } moving && moving.Round == rounds[r].Id)
                        HudDraw.Bar(new Rect(cell.x + Pad, cell.yMax - 3f, cell.width - 2 * Pad, 3f), moving.Progress, HudDraw.Ok);
                    if (marked && r < 9)
                        GUI.Label(new Rect(cell.x + HudDraw.Frame + 1f, cell.y + NameHeight, 12f, NameHeight), $"{r + 1}", key);
                }
                badge?.Invoke(gun, new Rect(row.xMax - Pad - BadgeWidth, cells, BadgeWidth, CellHeight));
                if (g > 0)
                    top -= Pad;
            }
            return bottom - top;
        }

        private static void Cell(Rect cell, (string Id, string Name, string Type, string Icon, int Left, int Ready) round, bool isLoaded, bool isNext, bool isWaiting)
        {
            Color before = GUI.color;
            if (isNext)
                HudDraw.Outline(cell, isWaiting ? HudDraw.Blocked : HudDraw.Next);

            Color tint = round.Left == 0 ? new Color(1f, 1f, 1f, 0.35f) : before;
            GUI.color = tint;
            GUI.Label(new Rect(cell.x, cell.y, cell.width, NameHeight), round.Name, name);
            var art = new Rect(cell.x + Pad, cell.y + NameHeight, cell.width - 2 * Pad, cell.height - NameHeight - CountHeight);
            if (Icon(round.Icon) is { } icon)
                HudDraw.Image(art, icon, tint);
            else
                GUI.Label(art, Loadout.Types.FirstOrDefault(t => t.Type == round.Type).Label ?? round.Type, label);
            string shells = Loadout.IsReady != null ? $"{round.Ready}/{round.Left}" : $"{round.Left}";
            count!.fontSize = System.Math.Clamp((int)(cell.width / (0.62f * shells.Length)), 8, CountFont); // 0.62 roughly the width of a bold digit
            GUI.Label(new Rect(cell.x, cell.yMax - CountHeight, cell.width, CountHeight), shells, count);
            GUI.color = before;

            if (isLoaded)
                HudDraw.Fill(new Rect(cell.x + Pad, cell.yMax - CountHeight - 3f, cell.width - 2 * Pad, 3f), HudDraw.Ok);
        }

        /// <summary>The icon under <paramref name="key"/> (see <see cref="Loadout.IconOf"/>); null if there's none or it didn't load.</summary>
        private static Texture2D? Icon(string key) =>
            Loadout.Icons.TryGetValue(key, out byte[]? png) ? HudDraw.Icon("rounds/" + key, png) : null;
    }
}
