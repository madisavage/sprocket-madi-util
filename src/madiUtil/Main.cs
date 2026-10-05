using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppProperties;
using Il2CppSprocket.UI;
using Il2CppSprocket.Vehicles.CrewSystems;
using Il2CppSprocket.Vehicles.CrewSystems.Editor;
using MelonLoader;

[assembly: MelonInfo(typeof(MadiUtil.Main), "madiUtil", "26.10.1", "madi")]
[assembly: MelonGame("HD", "Sprocket")]

[assembly: AssemblyMetadata("Sprocket.Mod.Id", "madi.madiutil")]
[assembly: AssemblyMetadata("Sprocket.Mod.DisplayName", "madiUtil")]
[assembly: AssemblyMetadata("Sprocket.Mod.Description", "Adds an editable base efficiency field to the crew seat edit pane.")]
[assembly: AssemblyMetadata("Sprocket.Mod.Authors", "madi")]
[assembly: AssemblyMetadata("Sprocket.Mod.Repository", "madisavage/madiUtil")]
[assembly: AssemblyMetadata("Sprocket.Mod.Category", "gameplay")]
[assembly: AssemblyMetadata("Sprocket.Mod.License", "GPL-3.0-only")]

namespace MadiUtil
{
    // MelonLoader applies every [HarmonyPatch] in the assembly on load.
    public sealed class Main : MelonMod
    {
    }

    [HarmonyPatch(typeof(CrewSeatEditor), "OnGUI")]
    internal static class CrewSeatEditorOnGuiPatch
    {
        // Int, Base Efficiency; One Key:Value pair for each seat
        private static readonly Dictionary<IntPtr, FloatProperty> properties = new();

        private static void Postfix(CrewSeatEditor __instance, IGUILayout __0)
        {
            CrewSeatBlueprint? blueprint = __instance.crewBlueprint;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (blueprint == null || drawer == null)
                return;
            // Initialize Property when not Existing
            if (!properties.TryGetValue(blueprint.Pointer, out FloatProperty? property))
            {
                property = new FloatProperty(
                    "Base efficiency",
                    (Func<float>)(() => blueprint.BaseEfficiency),
                    (Action<float>)(value => blueprint.BaseEfficiency = value),
                    "Crew member base efficiency multiplier.");
                property.Min = 0f;
                property.Max = 100f;
                property.Step = 0.25f;
                properties[blueprint.Pointer] = property;
            }

            drawer.Float(property);
        }
    }
}
