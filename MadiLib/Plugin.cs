using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace MadiLib
{
    /// <summary>
    /// BepInEx entry point for the shared library. Starts keybind polling and
    /// HUD drawing, and sets up the "MadiLib" Settings tab. Plugins using it declare
    /// <c>[BepInDependency("madi.madilib")]</c> so this loads first.
    /// </summary>
    [BepInPlugin("madi.madilib", "MadiLib", "26.10.12")]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;

        public override void Load()
        {
            Logger = Log;
            ClassInjector.RegisterTypeInIl2Cpp<MadiLibMenu>();
            AddComponent<KeybindPoller>();
            AddComponent<HudDrawer>();
            new Harmony("madi.madilib").PatchAll();
        }
    }
}
