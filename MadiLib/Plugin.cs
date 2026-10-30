using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine.UI;

namespace MadiLib
{
    /// <summary>
    /// BepInEx entry point for the shared library. Starts keybind polling and
    /// HUD drawing, and sets up the "MadiLib" Settings tab. Plugins using it declare
    /// <c>[BepInDependency("madi.madilib")]</c> so this loads first.
    /// </summary>
    [BepInPlugin("madi.madilib", "MadiLib", ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static ConfigEntry<float> ScrollSensitivity = null!;
        internal static ConfigEntry<bool> Profiling = null!;

        /// <summary>Starts keybind polling and HUD drawing, and registers the Settings tab.</summary>
        public override void Load()
        {
            Logger = Log;
            ScrollSensitivity = Config.Bind("Dropdowns", "Scroll speed", 7f, new ConfigDescription(
                "How far one mouse wheel step scrolls an open dropdown list, for every dropdown in the game. 7 matches the edit panel; the game's dropdowns use 0.05.",
                new AcceptableValueRange<float>(1f, 300f), new SoftMax(30f), new Step(0.5f)));
            Profiling = Config.Bind("Debug", "Profile", false,
                "Every 5 seconds, logs how long each plugin's hooks took, slowest first, to BepInEx/LogOutput.log. Costs a little frame rate while on.");
            SettingsTab.AddSection("MadiLib", "MadiLib", ScrollSensitivity, Profiling);
            ClassInjector.RegisterTypeInIl2Cpp<MadiLibMenu>();
            AddComponent<KeybindPoller>();
            AddComponent<HudDrawer>();
            AddComponent<ProfileReporter>();
            new Harmony("madi.madilib").PatchAll();
        }
    }

    /// <summary>Speeds up mouse wheel scrolling in open dropdown lists, which the game makes very slow (0.3 px a step).</summary>
    /// <remarks>TMP_Dropdown.Show is inlined past Harmony, so this catches the scroll itself.</remarks>
    [HarmonyPatch(typeof(ScrollRect), nameof(ScrollRect.OnScroll))]
    internal static class DropdownScrollPatch
    {
        private static void Prefix(ScrollRect __instance)
        {
            if (__instance.name == "Template")
                __instance.scrollSensitivity = Plugin.ScrollSensitivity.Value;
        }
    }
}
