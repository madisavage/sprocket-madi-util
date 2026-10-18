extern alias UnityCoreModule;

using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using Sprocket.Blueprints;
using Sprocket.SettingConfiguration;
using Sprocket.UI;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.CrewSystems.Editor;
using Sprocket.Vehicles.Weapons;
using Object = UnityCoreModule::UnityEngine.Object;

namespace MadiUtil
{
    /// <summary>
    /// BepInEx entry point. Applies every Harmony patch in this assembly and
    /// registers keybinds with MadiLib.
    /// </summary>
    [BepInPlugin("madi.madiutil", "madiUtil", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        public override void Load()
        {
            new Harmony("madi.madiutil").PatchAll();
            Keybinds.Register(this, "Save vehicle", "Ctrl+S", SaveVehicle);
            SettingsTab.AddSection(ui =>
            {
                ui.Header("madiUtil");
                ui.Bool(unlockEras);
            });
        }

        // The game's own setting, which its Settings screen doesn't show. Edits the
        // open Settings screen's working profile, which Apply and Accept save.
        private static readonly BoolProperty unlockEras = new(
            "Unlock scenario eras",
            (Func<bool>)(() => EditedSettings()?.UnlockScenarioEras ?? false),
            (Action<bool>)(value =>
            {
                if (EditedSettings() is { } general)
                    general.UnlockScenarioEras = value;
            }),
            "Lets scenarios use vehicles from any era.");

        /// <summary>The general settings the open Settings screen is editing.</summary>
        private static Sprocket.GeneralSettings? EditedSettings() => Object.FindObjectOfType<SettingsMenu>()?.editProfile?.General;

        /// <summary>
        /// Saves the vehicle open in the designer, the same as its save button.
        /// Does nothing outside the designer or while saving isn't possible.
        /// </summary>
        private static void SaveVehicle()
        {
            VehicleDesignerCore? designer = Object.FindObjectOfType<VehicleDesignerCore>();
            if (designer != null && designer.DesignIOPossible)
                designer.RequestSave();
        }
    }

    /// <summary>
    /// Adds a "Base efficiency" slider to the crew seat editor panel.
    /// </summary>
    [HarmonyPatch(typeof(CrewSeatEditor), "OnGUI")]
    internal static class CrewSeatEditorOnGuiPatch
    {
        // Seat blueprint, Base efficiency; one entry for each seat
        private static readonly ObjectCache<FloatProperty> properties = new();

        /// <summary>
        /// Runs after the game draws the crew seat panel and appends the slider,
        /// reusing one <see cref="FloatProperty"/> per seat blueprint.
        /// </summary>
        /// <param name="__instance">The crew seat editor being drawn.</param>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        private static void Postfix(CrewSeatEditor __instance, IGUILayout __0)
        {
            CrewSeatBlueprint? blueprint = __instance.crewBlueprint;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (blueprint == null || drawer == null)
                return;
            drawer.Float(properties.Get(blueprint, () => new FloatProperty(
                "Base efficiency",
                (Func<float>)(() => blueprint.BaseEfficiency),
                (Action<float>)(value => blueprint.BaseEfficiency = value),
                "Crew member base efficiency multiplier.")
            {
                Min = 0f,
                Max = 100f,
                Step = 0.25f
            }));
        }
    }

    /// <summary>
    /// Adds elevation and azimuth torque multiplier sliders to the laying drive
    /// editor panel.
    /// </summary>
    [HarmonyPatch(typeof(LayingDriveEditor), "OnGUI")]
    internal static class LayingDriveEditorOnGuiPatch
    {
        // Axis, Torque multiplier; one entry for each axis
        private static readonly ObjectCache<FloatProperty> properties = new();

        /// <summary>
        /// Runs after the game draws the laying drive panel and appends one
        /// slider per axis.
        /// </summary>
        /// <param name="__instance">The laying drive editor being drawn.</param>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        private static void Postfix(LayingDriveEditor __instance, IGUILayout __0)
        {
            BlueprintSlot<LayingDriveBlueprint>? slot = __instance.Component?.BlueprintSlot;
            LayingDriveBlueprint? blueprint = slot?.Blueprint;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (slot == null || blueprint == null || drawer == null)
                return;

            drawer.Float(GetProperty(slot, blueprint.Elevation, "Elevation torque multiplier"));
            drawer.Float(GetProperty(slot, blueprint.Azimuth, "Azimuth torque multiplier"));
        }

        /// <summary>
        /// Returns the cached slider property for <paramref name="axis"/>,
        /// creating it on first use. Changing the value marks the blueprint
        /// slot modified so the game knows the design changed.
        /// </summary>
        /// <param name="slot">The blueprint slot that owns the axis.</param>
        /// <param name="axis">The elevation or azimuth axis to edit.</param>
        /// <param name="name">The slider label.</param>
        private static FloatProperty GetProperty(BlueprintSlot<LayingDriveBlueprint> slot, LayingDriveBlueprint.Axis axis, string name) =>
            properties.Get(axis, () => new FloatProperty(
                name,
                (Func<float>)(() => axis.TorqueMultiplier),
                (Action<float>)(value =>
                {
                    axis.TorqueMultiplier = value;
                    slot.MarkModified();
                }),
                "Laying drive axis torque multiplier.")
            {
                Min = 0f,
                Max = 100f,
                Step = 0.25f
            });
    }
}
