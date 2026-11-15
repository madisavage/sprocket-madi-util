using HarmonyLib;
using Sprocket.Gameplay;

namespace MadiLib
{
    /// <summary>
    /// Pauses the vehicle designer's hotkeys while a text field has focus.
    /// The game pauses them for its own fields by turning each edit into an
    /// editor operation, which mod fields can't safely join, so typing a
    /// space in one would otherwise leave for driving.
    /// </summary>
    [HarmonyPatch]
    internal static class TypingGuard
    {
        /// <summary>Space: leaving the designer for driving.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(VehicleDesignerPlayerState), nameof(VehicleDesignerPlayerState.TryExitEditor))]
        private static bool ExitEditor(ref bool __result)
        {
            // While typing, the game's exit is skipped and reports it didn't leave
            __result = !Keybinds.Typing();
            return __result;
        }

        /// <summary>Every other designer hotkey: delete, duplicate, select all, transform and so on.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Update))]
        private static bool EditorInput()
        {
            using var _ = Profile.Measure("MadiLib: typing guard");
            // While typing, the game's hotkey handling is skipped for this frame
            return !Keybinds.Typing();
        }
    }
}
