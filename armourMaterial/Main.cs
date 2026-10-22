using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using DynamicGUI;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using Sprocket.PlateStructures.Blueprints;
using Sprocket.UI;
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.PlateStructures.Design.Generation;

namespace ArmourMaterial
{
    /// <summary>
    /// An armour material: its tech ID (the file's <c>type</c>), display name,
    /// invention date and properties. Hardness and spall are NaN when the file
    /// leaves them out.
    /// </summary>
    internal sealed record Material(string Id, string Name, string Date, float Rha, float Hardness, float Spall, float Density, float Cost)
    {
        /// <summary>Protection per kilogram relative to RHA: rhaFactor over density, with steel's density as 1.</summary>
        public float RhaPerKg => Rha * 7850f / Density;
    }

    /// <summary>
    /// BepInEx entry point. Puts a material dropdown in the plate structure
    /// editors in place of the game's "Use RHA" toggle, listing every armour material in the game's
    /// Technology folder.
    /// </summary>
    [BepInPlugin("madi.armourmaterial", "armourMaterial", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static List<Material> Materials = new();
        // The Sort by dropdown's choice, kept across sessions; not on the Settings tab, the dropdown sets it.
        internal static ConfigEntry<string> SortBy = null!;

        public override void Load()
        {
            Logger = Log;
            SortBy = Config.Bind("Dropdown", "Sort by", "Protection", "Material dropdown order, as picked in its Sort by dropdown.");
            Materials = LoadMaterials(Path.Combine(Paths.GameRootPath, "Sprocket_Data", "StreamingAssets", "Technology"));
            Logger.LogInfo($"Found {Materials.Count} armour materials.");
            new Harmony("madi.armourmaterial").PatchAll();
        }

        /// <summary>
        /// The tech files in <paramref name="directory"/> that are armour
        /// materials. A material is a tech whose properties have
        /// an <c>rhaFactor</c>; engines, cannons, transmissions and the like
        /// don't.
        /// </summary>
        internal static List<Material> LoadMaterials(string directory)
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            var materials = new List<Material>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), options);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("properties", out JsonElement properties) || !properties.TryGetProperty("rhaFactor", out _))
                        continue;
                    string name = Path.GetFileNameWithoutExtension(path);
                    // "RolledHomogeneousArmour" -> "Rolled Homogeneous Armour"; names with spaces are already readable.
                    if (!name.Contains(' '))
                        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
                    float Get(string key, float fallback) => properties.TryGetProperty(key, out JsonElement v) ? v.GetSingle() : fallback;
                    materials.Add(new Material(root.GetProperty("type").GetString()!, name,
                        root.TryGetProperty("date", out JsonElement date) ? date.GetString() ?? "" : "",
                        Get("rhaFactor", 1f), Get("hardness", float.NaN), Get("spallFactor", float.NaN), Get("density", 7850f), Get("costMultiplier", 1f)));
                }
                catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    Logger.LogWarning($"Skipping {Path.GetFileName(path)}: {e.Message}");
                }
            }
            return materials;
        }
    }

    /// <summary>
    /// Tracks the plate structure panel being drawn. If it never drew the
    /// game's RHA toggle (where <see cref="HideRhaTogglePatch"/> puts the
    /// Material dropdown), draws the dropdown at the end instead.
    /// </summary>
    [HarmonyPatch]
    internal static class DrawingPatch
    {
        // Panels nest (OnGUI may call OnEditGUI), so count rather than flag.
        private static int depth;
        internal static PlateStructureEditor? Editor;
        internal static bool Placed;

        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI)),
            AccessTools.Method(typeof(FreeformPlateStructureEditor), nameof(FreeformPlateStructureEditor.OnEditGUI)),
            AccessTools.Method(typeof(GeneratedCompartmentEditor), nameof(GeneratedCompartmentEditor.OnEditGUI)),
        };

        private static void Prefix(PlateStructureEditor __instance)
        {
            if (depth++ == 0)
            {
                Editor = __instance;
                Placed = false;
            }
        }

        /// <param name="__instance">The plate structure editor being drawn.</param>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        private static Exception? Finalizer(Exception? __exception, PlateStructureEditor __instance, IGUILayout __0)
        {
            if (--depth == 0)
            {
                if (__exception == null && !Placed && __0?.TryCast<IGUIElementDrawer>() is { } drawer)
                    MaterialDropdown.Draw(drawer, __instance);
                Editor = null;
            }
            return __exception;
        }

        internal static bool Drawing => depth > 0;
    }

    /// <summary>The Sort by and Material dropdowns: every armour material invented by the vehicle's era.</summary>
    internal static class MaterialDropdown
    {
        // Sort by choices: highest first, except names A to Z and dates oldest first. Missing values go last.
        private static readonly (string Name, Func<Material, IComparable> Key, bool Descending)[] sorts =
        {
            ("Protection", m => m.Rha, true),
            ("Protection per kg", m => m.RhaPerKg, true),
            ("Hardness", m => Missing(m.Hardness), true),
            ("Spall factor", m => Missing(m.Spall), true),
            ("Density", m => m.Density, true),
            ("Cost", m => m.Cost, true),
            ("Invented", m => m.Date, false),
            ("Name", m => m.Name, false),
        };

        // Sorts NaN below every value, so it's last when sorting highest first.
        private static float Missing(float value) => float.IsNaN(value) ? float.NegativeInfinity : value;

        internal static void Draw(IGUIElementDrawer drawer, PlateStructureEditor editor)
        {
            DrawingPatch.Placed = true;
            int sort = Math.Max(0, Array.FindIndex(sorts, s => s.Name == Plugin.SortBy.Value));
            var (_, key, descending) = sorts[sort];
            // The editor's tech is the vehicle's era; materials invented later aren't in it.
            IEnumerable<Material> invented = Plugin.Materials.Where(m => editor.GetTech(m.Id)?.GetFloat("rhaFactor", -1f) >= 0f);
            List<Material> available = (descending ? invented.OrderByDescending(key) : invented.OrderBy(key)).ThenBy(m => m.Name).ToList();
            if (available.Count == 0)
                return;
            StructureBlueprint blueprint = editor.Component.Blueprint;
            StringProperty[] readouts = Readouts(blueprint);
            EditorGui.Dropdown(
                drawer,
                "Sort by",
                sorts.Select(s => s.Name).ToList(),
                sort,
                i =>
                {
                    Plugin.SortBy.Value = sorts[i].Name;
                    editor.RequestRedraw();
                },
                "Order of the Material list. Highest first, except names A to Z and dates oldest first.");
            EditorGui.Dropdown(
                drawer,
                "Material",
                available.Select(m => m.Name).ToList(),
                available.FindIndex(m => m.Id == blueprint.armourTechID),
                i =>
                {
                    Set(editor, blueprint, available[i].Id);
                    EditorGui.Refresh(readouts);
                },
                "Armour material of this structure, from the game's Technology folder, invented by the vehicle's era.");
            foreach (StringProperty readout in readouts)
                drawer.Readonly(readout);
        }

        // Blueprint, its readout rows; cached so the rows keep their callbacks between redraws
        private static readonly ObjectCache<StringProperty[]> readouts = new();

        /// <summary>The selected material's numbers, as rows under the dropdown.</summary>
        private static StringProperty[] Readouts(StructureBlueprint blueprint) => readouts.Get(blueprint, () =>
        {
            Material? Selected() => Plugin.Materials.FirstOrDefault(m => m.Id == blueprint.armourTechID);
            string Show(Func<Material, string> format) => Selected() is { } m ? format(m) : "-";
            static string Value(float v, string format) => float.IsNaN(v) ? "-" : v.ToString(format);
            return new[]
            {
                EditorGui.Readout("Protection", () => Show(m => $"{m.Rha:0.###}x RHA, {m.RhaPerKg:0.##}x per kg"), "rhaFactor: protection per mm against rolled armour. Per kg divides by density, with steel's as 1."),
                EditorGui.Readout("Hardness", () => Show(m => Value(m.Hardness, "0.###")), "hardness, from the material's file."),
                EditorGui.Readout("Spall factor", () => Show(m => Value(m.Spall, "0.######")), "spallFactor: higher throws more spall when penetrated."),
                EditorGui.Readout("Density", () => Show(m => $"{m.Density:0} kg/m³")),
                EditorGui.Readout("Cost", () => Show(m => $"x{m.Cost:0.##}"), "costMultiplier, from the material's file."),
                EditorGui.Readout("Invented", () => Show(m => m.Date)),
            };
        });

        private static void Set(PlateStructureEditor editor, StructureBlueprint blueprint, string id)
        {
            // The frame records the blueprint for undo; it's an Il2Cpp IDisposable, so no using.
            var frame = editor.NewBlueprintEditFrame("Armour material", blueprint, null, editor.GetNewOperationGroupID(), VehicleOpExecutionMode.Synchronous);
            try
            {
                blueprint.armourTechID = id;
            }
            finally
            {
                frame.Dispose();
            }
            editor.Component.SetArmourMaterial(id);
        }
    }

    /// <summary>Draws the Material dropdown in place of the game's "Use RHA" toggle in plate structure panels.</summary>
    [HarmonyPatch]
    internal static class HideRhaTogglePatch
    {
        // ToggleField(label, ...) overloads, and Bool(BoolProperty).
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(DynamicGUILayout).GetMethods().Where(m => m.Name is nameof(DynamicGUILayout.ToggleField) or nameof(DynamicGUILayout.Bool));

        private static bool Prefix(DynamicGUILayout __instance, object[] __args)
        {
            if (!DrawingPatch.Drawing || DrawingPatch.Editor == null)
                return true;
            string? label = __args[0] switch
            {
                string s => s,
                Property p => p.Name,
                _ => null,
            };
            if (label == null || !label.Contains("RHA", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!DrawingPatch.Placed)
                MaterialDropdown.Draw(__instance.Cast<IGUIElementDrawer>(), DrawingPatch.Editor);
            return false;
        }
    }
}
