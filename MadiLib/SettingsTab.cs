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
    /// The "MadiLib" tab in the game's Settings screen: the keybinds from
    /// <see cref="Keybinds"/>, then each section plugins add here.
    /// </summary>
    [HarmonyPatch]
    public static class SettingsTab
    {
        private static readonly List<Action<IGUIElementDrawer>> sections = new();
        // Config entry, its property; one Key:Value pair per entry drawn, cached so their callbacks stay alive
        private static readonly Dictionary<ConfigEntryBase, FloatProperty> floats = new();
        private static readonly Dictionary<ConfigEntryBase, BoolProperty> bools = new();

        /// <summary>
        /// Adds a section to the tab. Apply does nothing, so changes should
        /// save at once (a BepInEx <c>ConfigEntry</c> does by default).
        /// </summary>
        /// <param name="draw">
        /// Called each time the tab is drawn. Cache any properties it draws so
        /// their callbacks stay alive.
        /// </param>
        public static void AddSection(Action<IGUIElementDrawer> draw) => sections.Add(draw);

        /// <summary>
        /// Adds a section with <paramref name="header"/> and one control per
        /// entry, labelled with its config key and described by its config
        /// description. A float entry gets a slider, limited by an
        /// <see cref="AcceptableValueRange{T}"/> and stepped by a <see cref="Step"/>
        /// tag when its description has them; a bool entry gets a toggle.
        /// </summary>
        public static void AddSection(string header, params ConfigEntryBase[] entries) => AddSection(ui =>
        {
            ui.Header(header);
            foreach (ConfigEntryBase entry in entries)
            {
                if (entry is ConfigEntry<float> number)
                    ui.Float(FloatFor(number));
                else if (entry is ConfigEntry<bool> toggle)
                    ui.Bool(BoolFor(toggle));
            }
        });

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

        /// <summary>Draws the tab: keybinds, then each section.</summary>
        /// <param name="page">The Settings tab being drawn.</param>
        /// <param name="layout">The layout the game is drawing the tab into.</param>
        internal static void Draw(MadiLibMenu page, IGUILayout layout)
        {
            if (layout.TryCast<IGUIElementDrawer>() is not { } ui)
                return;
            Keybinds.Draw(page, ui);
            foreach (Action<IGUIElementDrawer> section in sections)
                section(ui);
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
    /// Slider step for a float setting drawn by <see cref="SettingsTab.AddSection(string, ConfigEntryBase[])"/>.
    /// Pass it in the <see cref="ConfigDescription"/> tags.
    /// </summary>
    public sealed record Step(float Value);

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

        public override void DisplayGUI(IGUILayout layout) => SettingsTab.Draw(this, layout);
        public override void Update() => Keybinds.Capture(this);
        public override void ClearGUI() => Keybinds.StopCapture();
        public override void Apply() { }
        public override void Cancel() => Keybinds.StopCapture();
    }
}
