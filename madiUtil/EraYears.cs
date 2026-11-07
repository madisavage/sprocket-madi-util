extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using DynamicGUI;
using HarmonyLib;
using Sprocket.TechTrees;
using Sprocket.VehicleDesigner;
using Sprocket.VehicleDesigner.Access;
using Sprocket.VehicleDesigner.UI;
using Sprocket.Vehicles;
using TMPro;
using Transform = UnityCoreModule::UnityEngine.Transform;

namespace MadiUtil
{
    /// <summary>
    /// Lets a vehicle's tech date be any year inside its era: the designer's era
    /// dropdown lists each year of every era ("1943 (Midwar)") in place of the eras,
    /// up to the latest tech's invention. A row sets the tech date to 1 January of
    /// that year (the era's start in its first year).
    /// The game saves the date in the file's header (<c>creationDate</c>), so the
    /// year needs no storage of its own and players without madiUtil keep it.
    /// </summary>
    internal static class EraYears
    {
        // shortcut: an era longer than this (e.g. a last era running to 9999) lists only its first years; raise it if an era ever needs more.
        private const int MaxYears = 60;

        // The date each dropdown row sets, in order
        private static readonly List<DateOnly> rows = new();
        private static EditorConfigurationSelectorPlugin? selector;
        private static bool warned, unlabelled;
        // Invention date of the latest tech in the game's Technology folder; years after it unlock nothing new
        internal static DateOnly LastTech = DateOnly.MaxValue;

        /// <summary>
        /// The latest <c>date</c> among the tech files in <paramref name="directory"/>,
        /// read at launch so installs with added tech get their later years.
        /// Unreadable files are skipped; with none readable, no limit.
        /// </summary>
        internal static DateOnly LatestTech(string directory)
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            DateOnly latest = DateOnly.MinValue;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), options);
                    if (doc.RootElement.TryGetProperty("date", out JsonElement value)
                        && DateOnly.TryParseExact(value.GetString(), "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
                        && date > latest)
                        latest = date;
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                }
            }
            return latest == DateOnly.MinValue ? DateOnly.MaxValue : latest;
        }

        /// <summary>
        /// The date each year of an era stands for: 1 January, or the era's start in
        /// its first year. Stops at <paramref name="end"/>, at <paramref name="limit"/>
        /// and after <see cref="MaxYears"/> years.
        /// </summary>
        internal static IEnumerable<DateOnly> YearStarts(DateOnly start, DateOnly end, DateOnly limit)
        {
            for (int year = start.Year; year <= end.Year && year < start.Year + MaxYears; year++)
            {
                DateOnly date = year == start.Year ? start : new DateOnly(year, 1, 1);
                if (date > limit)
                    yield break;
                yield return date;
            }
        }

        private static DateOnly ToDate(TechDate date)
        {
            int year = Math.Clamp((int)date.Year, 1, 9999), month = Math.Clamp((int)date.Month, 1, 12);
            return new DateOnly(year, month, Math.Clamp((int)date.Day, 1, DateTime.DaysInMonth(year, month)));
        }

        private static TechDate ToTech(DateOnly date) => new((ushort)date.Year, (byte)date.Month, (byte)date.Day);

        /// <summary>
        /// Replaces the dropdown's options, which the game just set to one per era,
        /// with the years of each era. Leaves the game's list alone if an
        /// option isn't an era name.
        /// </summary>
        internal static void Build(EditorConfigurationSelectorPlugin plugin)
        {
            rows.Clear();
            selector = null;
            var shown = plugin.displayedOptions;
            DropDown? dropdown = plugin.dropdown;
            IVehicleEditorSourceConfigurations? loader = plugin.configLoader;
            if (shown == null || dropdown == null || loader == null)
                return;
            TechDate techLimit = loader.TechnologyLimit;
            DateOnly limit = techLimit.Valid && ToDate(techLimit) < LastTech ? ToDate(techLimit) : LastTech;
            var labels = new Il2CppSystem.Collections.Generic.List<string>();
            for (int i = 0; i < shown.Count; i++)
            {
                string name = shown[i];
                int era = VehicleClassifications.GetEraIndexByName(name);
                if (era < 0)
                {
                    if (!warned)
                        Plugin.Logger.LogWarning($"Era dropdown option \"{name}\" isn't an era name; not adding years to the era dropdown.");
                    warned = true;
                    rows.Clear();
                    return;
                }
                DateOnly start = ToDate(VehicleClassifications.GetEraByName(name).StartDate);
                foreach (DateOnly date in YearStarts(start, ToDate(VehicleClassifications.GetEraEndDate(era)), limit))
                {
                    labels.Add($"{date.Year} ({name})");
                    rows.Add(date);
                }
            }
            selector = plugin;
            Relabel(dropdown);
            dropdown.SetOptions(labels.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>());
            Sync();
        }

        /// <summary>
        /// Renames the dropdown's "Era" heading to "Year": the nearest text reading
        /// "Era" within three parents of the dropdown (it isn't in the game's
        /// localization files, so it's in the UI prefab).
        /// </summary>
        private static void Relabel(DropDown dropdown)
        {
            Transform? parent = dropdown.transform;
            for (int up = 0; up < 3 && parent != null; up++, parent = parent.parent)
                foreach (TMP_Text text in parent.GetComponentsInChildren<TMP_Text>(true))
                    if (text.text.Trim().TrimEnd(':').Equals("Era", StringComparison.OrdinalIgnoreCase))
                    {
                        text.text = "Year";
                        return;
                    }
            if (!unlabelled)
                Plugin.Logger.LogWarning("Found no \"Era\" label near the era dropdown; it keeps its name.");
            unlabelled = true;
        }

        /// <summary>Our rows are showing: the game hasn't replaced the options since <see cref="Build"/>.</summary>
        private static bool Active(out EditorConfigurationSelectorPlugin plugin)
        {
            plugin = selector!;
            return selector?.dropdown is { } dropdown && selector.configLoader != null && dropdown.OptionCount == rows.Count;
        }

        /// <summary>
        /// Handles a pick from the dropdown: sets the row's date and reloads the
        /// design, in place of the game's era change.
        /// </summary>
        /// <returns>False when the pick is handled here and the game's code must not run.</returns>
        internal static bool Pick(EditorConfigurationSelectorPlugin plugin, ref int index)
        {
            if (!Active(out EditorConfigurationSelectorPlugin active) || active.Pointer != plugin.Pointer || index < 0 || index >= rows.Count)
                return true;
            IVehicleEditorSourceConfigurations loader = plugin.configLoader;
            DateOnly date = rows[index];
            // Leaving the designer fires the dropdown's change event (a reset to row 0, the first era's first year); only a loaded design takes picks
            var state = loader.TryCast<VehicleDesignerCore>()?.loadState;
            if (state != VehicleDesignerCore.LoadState.Loaded)
            {
                Plugin.Logger.LogInfo($"Year pick {date:yyyy.MM.dd} ignored: designer {state?.ToString() ?? "not found"}.");
                return false;
            }
            Plugin.Logger.LogInfo($"Year pick {date:yyyy.MM.dd} (was {ToDate(loader.TechnologyDate):yyyy.MM.dd}).");
            if (date != ToDate(loader.TechnologyDate))
            {
                loader.TechnologyDate = ToTech(date);
                loader.Reload();
                plugin.configChangedSFX?.Trigger();
            }
            return false;
        }

        /// <summary>
        /// Shows the row for the current tech date: the latest row on or before it,
        /// so a vehicle saved at an era's last day (the game's own date) shows that
        /// year. Only shows it; the date changes when the player picks a row.
        /// Run every designer update, as the game sets the selection by era index.
        /// </summary>
        internal static void Sync()
        {
            if (!Active(out EditorConfigurationSelectorPlugin plugin) || plugin.dropdown.Shown)
                return;
            int row = Shown(plugin);
            if (row >= 0 && plugin.dropdown.SelectedIndex != row)
                plugin.dropdown.SetSelectedWithoutNotify(row);
        }

        /// <summary>The row for the current tech date: the latest row on or before it; -1 for none.</summary>
        private static int Shown(EditorConfigurationSelectorPlugin plugin)
        {
            DateOnly date = ToDate(plugin.configLoader.TechnologyDate);
            int row = -1;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] <= date && (row < 0 || rows[i] > rows[row]))
                    row = i;
            return row;
        }

        /// <summary>
        /// The game selects its era by its own option index (e.g. on leaving the
        /// designer, when <see cref="Sync"/> no longer runs); on our dropdown,
        /// shows the row for the current date instead.
        /// </summary>
        internal static void Translate(DropDown dropdown, ref int index)
        {
            if (Active(out EditorConfigurationSelectorPlugin plugin) && dropdown.Pointer == plugin.dropdown.Pointer && Shown(plugin) is int row and >= 0)
                index = row;
        }
    }

    [HarmonyPatch(typeof(EditorConfigurationSelectorPlugin), nameof(EditorConfigurationSelectorPlugin.SetOptions))]
    internal static class EraOptionsPatch
    {
        private static void Postfix(EditorConfigurationSelectorPlugin __instance) => EraYears.Build(__instance);
    }

    /// <summary>The dropdown's change handler; SetConfig itself may be inlined into it.</summary>
    [HarmonyPatch(typeof(EditorConfigurationSelectorPlugin), nameof(EditorConfigurationSelectorPlugin.SetConfigAsyncVoid))]
    internal static class EraPickPatch
    {
        private static bool Prefix(EditorConfigurationSelectorPlugin __instance, ref int __0) => EraYears.Pick(__instance, ref __0);
    }

    [HarmonyPatch(typeof(DropDown), nameof(DropDown.SetSelectedWithoutNotify))]
    internal static class EraSelectPatch
    {
        private static void Prefix(DropDown __instance, ref int __0) => EraYears.Translate(__instance, ref __0);
    }

    [HarmonyPatch(typeof(VehicleDesignerCore), nameof(VehicleDesignerCore.EditorUpdate))]
    internal static class EraSyncPatch
    {
        private static void Postfix() => EraYears.Sync();
    }
}
