using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Properties;
using Sprocket.UI;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.CrewSystems.Editor;

namespace MadiUtil
{
    [BepInPlugin("madi.madiutil", "madiUtil", "26.10.1")]
    public sealed class Plugin : BasePlugin
    {
        // BepInEx doesn't auto-apply patches like MelonLoader does.
        public override void Load() => new Harmony("madi.madiutil").PatchAll();
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
