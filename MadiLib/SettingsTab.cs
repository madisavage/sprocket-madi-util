using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Properties;
using Sprocket.SettingConfiguration;
using Sprocket.UI;

namespace MadiLib
{
    /// <summary>
    /// The "MadiLib" tab in the game's Settings screen: a subtab per feature
    /// area, each with the sections plugins add to it, then its keybinds from
    /// <see cref="Keybinds"/>. Sections with a header collapse and start
    /// collapsed; opening one closes the others in its subtab, so a subtab
    /// fits on screen (the Settings screen doesn't scroll).
    /// </summary>
    [HarmonyPatch]
    public static class SettingsTab
    {
        // Each section's subtab, header (null if it doesn't collapse) and drawer, in the order added
        private static readonly List<(string Subtab, string? Header, Action<IGUIElementDrawer> Draw)> sections = new();
        // Subtab, its open section; one Key:Value pair per subtab with a section open
        private static readonly Dictionary<string, string> open = new();
        // The subtab shown, for the session; null for the first
        private static string? selected;
        // Config entry, its property; one Key:Value pair per entry drawn, cached so their callbacks stay alive
        private static readonly Dictionary<ConfigEntryBase, FloatProperty> floats = new();
        private static readonly Dictionary<ConfigEntryBase, BoolProperty> bools = new();

        /// <summary>
        /// Adds a section to <paramref name="subtab"/>, which is created the
        /// first time it's named; subtabs show in the order first named. Apply
        /// does nothing, so changes should save at once (a BepInEx
        /// <c>ConfigEntry</c> does by default).
        /// </summary>
        /// <param name="subtab">The subtab, such as "Rounds"; add-on plugins pass their parent's to join it.</param>
        /// <param name="header">The collapsible header, or null for a section that doesn't collapse.</param>
        /// <param name="draw">
        /// Called each time the subtab is drawn, while the section is
        /// expanded. Cache any properties it draws so their callbacks stay alive.
        /// </param>
        public static void AddSection(string subtab, string? header, Action<IGUIElementDrawer> draw) => sections.Add((subtab, header, draw));

        /// <summary>
        /// Adds a section to <paramref name="subtab"/> with <paramref name="header"/>
        /// and one control per entry, labelled with its config key and described
        /// by its config description. A float entry gets a slider, limited by an
        /// <see cref="AcceptableValueRange{T}"/>, ended early by a <see cref="SoftMax"/>
        /// tag and stepped by a <see cref="Step"/> tag when its description has
        /// them; a bool entry gets a toggle.
        /// </summary>
        public static void AddSection(string subtab, string header, params ConfigEntryBase[] entries) => AddSection(subtab, header, ui =>
        {
            foreach (ConfigEntryBase entry in entries)
            {
                if (entry is ConfigEntry<float> number)
                    ui.Float(FloatFor(number));
                else if (entry is ConfigEntry<bool> toggle)
                    ui.Bool(BoolFor(toggle));
            }
        });

        /// <summary>Adds a section that doesn't collapse, in the "MadiLib" subtab. Kept for older plugins.</summary>
        public static void AddSection(Action<IGUIElementDrawer> draw) => AddSection("MadiLib", null, draw);

        /// <summary>Adds a collapsible section in a subtab named <paramref name="header"/>. Kept for older plugins.</summary>
        public static void AddSection(string header, Action<IGUIElementDrawer> draw) => AddSection(header, header, draw);

        /// <summary>Adds a section of config entries in a subtab named <paramref name="header"/>. Kept for older plugins.</summary>
        public static void AddSection(string header, params ConfigEntryBase[] entries) => AddSection(header, header, entries);

        private static FloatProperty FloatFor(ConfigEntry<float> entry)
        {
            if (floats.TryGetValue(entry, out FloatProperty? property))
                return property;

            property = new FloatProperty(
                entry.Definition.Key,
                (Func<float>)(() => entry.Value),
                (Action<float>)(value => entry.Value = value),
                entry.Description.Description);
            if (entry.Description.AcceptableValues is AcceptableValueRange<float> range)
            {
                property.Min = range.MinValue;
                property.Max = range.MaxValue;
            }
            if (entry.Description.Tags?.OfType<SoftMax>().FirstOrDefault() is { } softMax)
            {
                property.SoftMin = property.Min;
                property.SoftMax = softMax.Value;
                property.UseSoftLimits = true;
            }
            if (entry.Description.Tags?.OfType<Step>().FirstOrDefault() is { } step)
                property.Step = step.Value;
            return floats[entry] = property;
        }

        private static BoolProperty BoolFor(ConfigEntry<bool> entry)
        {
            if (!bools.TryGetValue(entry, out BoolProperty? property))
                bools[entry] = property = new BoolProperty(
                    entry.Definition.Key,
                    (Func<bool>)(() => entry.Value),
                    (Action<bool>)(value => entry.Value = value),
                    entry.Description.Description);
            return property;
        }

        /// <summary>Draws the tab: the subtab bar, then the selected subtab's sections and keybinds.</summary>
        /// <param name="page">The Settings tab being drawn.</param>
        /// <param name="layout">The layout the game is drawing the tab into.</param>
        internal static void Draw(MadiLibMenu page, IGUILayout layout)
        {
            if (layout.TryCast<IGUIElementDrawer>() is not { } ui)
                return;
            List<string> subtabs = sections.Select(s => s.Subtab).Concat(Keybinds.Subtabs).Distinct().ToList();
            if (subtabs.Count == 0)
                return;
            int index = Math.Max(0, subtabs.IndexOf(selected ?? ""));
            string subtab = subtabs[index];
            if (subtabs.Count > 1)
                ui.TabGroup((Action<int>)(i =>
                {
                    selected = subtabs[i];
                    Keybinds.StopCapture();
                    page.GUIRepaint = true;
                }), index, subtabs.ToArray());
            foreach (var (tab, header, draw) in sections)
                if (tab == subtab && (header == null || Expanded(page, ui, subtab, header)))
                    draw(ui);
            if (Keybinds.Subtabs.Contains(subtab) && Expanded(page, ui, subtab, "Keybinds"))
                Keybinds.Draw(page, ui, subtab);
        }

        /// <summary>Draws a section header; opening it closes the subtab's other sections.</summary>
        private static bool Expanded(MadiLibMenu page, IGUIElementDrawer ui, string subtab, string header)
        {
            bool shown = open.TryGetValue(subtab, out string? current) && current == header;
            ui.HeaderBar(header, shown, (Action<bool>)(value =>
            {
                if (value)
                    open[subtab] = header;
                else
                    open.Remove(subtab);
                page.GUIRepaint = true;
            }));
            return shown;
        }

        /// <summary>
        /// Runs before the Settings screen builds its tabs and appends our tab
        /// once. Skipped when no plugin has added anything to it.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SetupMenuButtons))]
        private static void AddTab(SettingsMenu __instance)
        {
            Il2CppReferenceArray<SettingsSubMenu>? menus = __instance.subMenus;
            if ((!Keybinds.Any && sections.Count == 0) || menus == null)
                return;
            foreach (SettingsSubMenu menu in menus)
                if (menu?.TryCast<MadiLibMenu>() != null)
                    return;

            var more = new Il2CppReferenceArray<SettingsSubMenu>(menus.Length + 1);
            for (int i = 0; i < menus.Length; i++)
                more[i] = menus[i];
            more[menus.Length] = new MadiLibMenu();
            __instance.subMenus = more;
        }
    }

    /// <summary>
    /// Slider step for a float setting drawn by <see cref="SettingsTab.AddSection(string, string, ConfigEntryBase[])"/>.
    /// Pass it in the <see cref="ConfigDescription"/> tags.
    /// </summary>
    public sealed record Step(float Value);

    /// <summary>
    /// Slider end for a float setting drawn by <see cref="SettingsTab.AddSection(string, string, ConfigEntryBase[])"/>,
    /// like the game's own sliders: typed values may go past it, up to the
    /// <see cref="AcceptableValueRange{T}"/> maximum. Pass it in the <see cref="ConfigDescription"/> tags.
    /// </summary>
    public sealed record SoftMax(float Value);

    /// <summary>
    /// The "MadiLib" tab in the game's Settings screen. Registered with the
    /// IL2CPP runtime so the game can treat it like its own tabs. Changes save
    /// immediately, so Apply does nothing.
    /// </summary>
    public sealed class MadiLibMenu : SettingsSubMenu
    {
        /// <summary>Wraps an existing native instance; used by Il2CppInterop.</summary>
        public MadiLibMenu(IntPtr ptr) : base(ptr) { }

        /// <summary>Creates a new tab with its native counterpart.</summary>
        public MadiLibMenu() : base(ClassInjector.DerivedConstructorPointer<MadiLibMenu>())
        {
            ClassInjector.DerivedConstructorBody(this);
            MenuName = "MadiLib";
        }

        /// <summary>Draws the keybinds and each plugin's section.</summary>
        public override void DisplayGUI(IGUILayout layout) => SettingsTab.Draw(this, layout);
        /// <summary>Listens for a key while a keybind is being rebound.</summary>
        public override void Update() => Keybinds.Capture(this);
        /// <summary>Stops rebinding when the tab closes.</summary>
        public override void ClearGUI() => Keybinds.StopCapture();
        /// <summary>Does nothing: changes already saved.</summary>
        public override void Apply() { }
        /// <summary>Stops rebinding.</summary>
        public override void Cancel() => Keybinds.StopCapture();
    }
}
