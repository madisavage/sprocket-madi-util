using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using MadiLib;
using Properties;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Cannons.Editor;

namespace MadiUtil
{
    /// <summary>
    /// An <b>Operating pressure</b> slider in the cannon panel, so a gun can be
    /// built for less than its era's chamber pressure (a low-pressure gun-launcher,
    /// as the Sheridan's). It sets the cannon's own saved pressure, which the
    /// game's interior ballistics already read, from 10% to 100% of the era's.
    /// The game sets it back to the era's when it applies the cannon's design
    /// (on reselecting the gun or editing a round), so the pick is kept in the
    /// cannon's description as <c>"pressure"</c> (beside rounds' <c>"rounds"</c>)
    /// and put back whenever the game recalculates or modifies the cannon.
    /// </summary>
    internal static class Pressure
    {
        /// <summary>Lowest pressure the slider goes to, as a share of the era's.</summary>
        internal const float MinShare = 0.1f;

        // Each cannon tech's date and operating pressure, oldest first
        internal static List<(DateOnly Date, uint Pressure)> Eras = new();

        /// <summary>
        /// The <c>operatingPressure</c> and <c>date</c> of each <c>"type": "cannon"</c>
        /// tech file in <paramref name="directory"/>, oldest first. Unreadable files are skipped.
        /// </summary>
        internal static List<(DateOnly Date, uint Pressure)> Read(string directory)
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            var eras = new List<(DateOnly, uint)>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), options);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("type", out JsonElement type) && type.GetString() == "cannon"
                        && root.TryGetProperty("date", out JsonElement date)
                        && DateOnly.TryParseExact(date.GetString(), "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                        && root.TryGetProperty("properties", out JsonElement properties)
                        && properties.TryGetProperty("operatingPressure", out JsonElement pressure) && pressure.TryGetUInt32(out uint value))
                        eras.Add((day, value));
                }
                catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
                {
                }
            }
            return eras.OrderBy(e => e.Item1).ToList();
        }

        // Cannon blueprint, its description and the pressure stored in it, so a recalculation doesn't parse JSON again
        private static readonly Dictionary<IntPtr, (string Text, uint? Pressure)> stored = new();

        /// <summary>The pressure picked for <paramref name="gun"/>, from its description; null for the era's.</summary>
        internal static uint? Stored(CannonBlueprint gun)
        {
            string text = gun.Description ?? "";
            if (stored.TryGetValue(gun.Pointer, out var cached) && cached.Text == text)
                return cached.Pressure;
            uint? pressure = Parse(text);
            stored[gun.Pointer] = (text, pressure);
            return pressure;
        }

        /// <summary>The <c>"pressure"</c> in a description; null without one or when it isn't JSON.</summary>
        internal static uint? Parse(string text)
        {
            try
            {
                return text.Length > 0 && JsonNode.Parse(text) is JsonObject json && json["pressure"] is JsonValue value && value.TryGetValue(out uint pressure)
                    ? pressure : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// <paramref name="text"/> with its <c>"pressure"</c> set to <paramref name="pressure"/>, other keys kept;
        /// null when it holds other text than JSON, which must be left as it is.
        /// </summary>
        internal static string? With(string text, uint pressure) => With(text, "pressure", pressure);

        /// <summary>
        /// <paramref name="text"/> with <paramref name="key"/> set to <paramref name="value"/> (removed for null), other keys kept;
        /// null when it holds other text than JSON, which must be left as it is.
        /// </summary>
        internal static string? With(string text, string key, JsonNode? value)
        {
            JsonObject json;
            try
            {
                json = text.Length == 0 ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return null;
            }
            if (value == null)
                json.Remove(key);
            else
                json[key] = value;
            return json.ToJsonString();
        }

        /// <summary>Sets <paramref name="gun"/>'s pressure and keeps it in its description.</summary>
        internal static void Set(CannonBlueprint gun, uint pressure)
        {
            gun.OperatingPressure = pressure;
            if (With(gun.Description ?? "", pressure) is { } text)
                gun.Description = text;
            else if (!warned.Contains(gun.Pointer) && warned.Add(gun.Pointer))
                Plugin.Logger.LogWarning($"Cannon {gun.Name}'s description isn't JSON, so its operating pressure can't be kept and the game may reset it.");
        }

        private static readonly HashSet<IntPtr> warned = new();

        /// <summary>Puts <paramref name="gun"/>'s picked pressure back if the game changed it; true if it did.</summary>
        internal static bool Restore(CannonBlueprint gun)
        {
            if (Stored(gun) is not uint pressure || gun.OperatingPressure == pressure)
                return false;
            gun.OperatingPressure = pressure;
            gun.physicalPropertiesDirty = true;
            return true;
        }

        /// <summary>The pressure of the latest era on or before <paramref name="date"/> (the first era's before any); null with none.</summary>
        internal static uint? Of(IReadOnlyList<(DateOnly Date, uint Pressure)> eras, DateOnly date)
        {
            if (eras.Count == 0)
                return null;
            uint pressure = eras[0].Pressure;
            foreach (var era in eras)
                if (era.Date <= date)
                    pressure = era.Pressure;
            return pressure;
        }
    }

    [HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    internal static class CannonPressurePatch
    {
        // Cannon blueprint and its era's pressure, its slider; a new slider when the era changes, as its limits can't be changed after
        private static readonly Dictionary<(IntPtr, uint), FloatProperty> properties = new();
        // Cannon blueprint, its breech sliders
        private static readonly ObjectCache<FloatProperty[]> breechFields = new();

        // Before the game's fields, so the section sits at the top of the panel
        private static void Prefix(CannonEditor __instance, IGUILayout __0)
        {
            var cannon = __instance.Component;
            var blueprint = cannon?.Blueprint;
            var slot = cannon?.blueprintSlot;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (blueprint == null || slot == null || drawer == null || EraYears.Designer == null)
                return;
            if (Pressure.Of(Pressure.Eras, EraYears.ToDate(EraYears.Designer.TechnologyDate)) is not uint era)
                return;
            if (Pressure.Restore(blueprint) | Breech.Apply(blueprint))
                slot.MarkModified();
            if (!EditorGui.Section(drawer, "madiUtil.gun", "Gun settings", __instance.RequestRedraw, open: true))
                return;
            void Change(Func<Breech.Settings, Breech.Settings> change)
            {
                Breech.Set(blueprint, change(Breech.Of(blueprint)));
                slot.MarkModified();
            }
            FloatProperty Field(string name, Func<Breech.Settings, float> get, Func<Breech.Settings, float, Breech.Settings> set, float min, float step, string tooltip) =>
                new(name, (Func<float>)(() => get(Breech.Of(blueprint))), (Action<float>)(v => Change(s => set(s, v))), tooltip)
                { Min = min, Max = 1f, Step = step };
            var fields = breechFields.Get(blueprint, () => new[]
            {
                Field("Breech width", s => s.X, (s, v) => s with { X = v }, Breech.MinScale, 0.01f, "Breech width as a share of the game's; its mass shrinks with it. Very thin (a few mm) stops the gun elevating."),
                Field("Breech height", s => s.Y, (s, v) => s with { Y = v }, Breech.MinScale, 0.01f, "Breech height as a share of the game's; its mass shrinks with it. Very thin (a few mm) stops the gun elevating."),
                Field("Breech length", s => s.Z, (s, v) => s with { Z = v }, Breech.MinScale, 0.01f, "Breech length as a share of the game's. The barrel starts at the breech's front, so a short one pulls the barrel back, which can jam it in the mantlet."),
                Field("Recoil", s => s.Recoil, (s, v) => s with { Recoil = v }, 0f, 0.05f, "Share of the gun's recoil that pushes the vehicle. 0 is a recoilless gun, venting its gas backward; real ones wasted most of it, so lower the Operating pressure to match."),
            });
            foreach (FloatProperty breechField in fields)
                drawer.Float(breechField);
            drawer.ToggleField("Backblast", Breech.Of(blueprint).Backblast, (Action<bool>)(on =>
            {
                Change(s => s with { Backblast = on });
                __instance.RequestRedraw();
            }), "A flash behind the gun as it fires, as from a recoilless gun's vented gas.");
            if (!properties.TryGetValue((blueprint.Pointer, era), out FloatProperty? field))
                properties[(blueprint.Pointer, era)] = field = new FloatProperty(
                    "Operating pressure",
                    (Func<float>)(() => blueprint.OperatingPressure),
                    (Action<float>)(value =>
                    {
                        Pressure.Set(blueprint, (uint)Math.Clamp(MathF.Round(value), Pressure.MinShare * era, era));
                        slot.MarkModified();
                    }),
                    $"Chamber pressure the gun is built for, up to its era's {era}. Lower fires every round slower: a low-pressure gun-launcher, as for a missile launched by a small charge.")
                {
                    Min = Pressure.MinShare * era,
                    Max = era,
                    Step = 500f
                };
            drawer.Float(field);
        }
    }

    /// <summary>Before the game works out the cannon's muzzle velocity and mass, its picked pressure goes back in.</summary>
    [HarmonyPatch(typeof(CannonBlueprint), nameof(CannonBlueprint.RecalculatePhysicalValues))]
    internal static class PressureRecalculatePatch
    {
        private static void Prefix(CannonBlueprint __instance)
        {
            Pressure.Restore(__instance);
            Breech.Apply(__instance);
        }

        // The breech scaled as set
        private static void Postfix(CannonBlueprint __instance) => Breech.Scale(__instance);
    }

    /// <summary>After the game applies a change to the cannon, which can set the era's pressure.</summary>
    [HarmonyPatch(typeof(CannonBlueprint), nameof(CannonBlueprint.OnModified))]
    internal static class PressureModifiedPatch
    {
        private static void Postfix(CannonBlueprint __instance)
        {
            Pressure.Restore(__instance);
            Breech.Apply(__instance);
        }
    }

    /// <summary>
    /// The game copies its era's cannon tech (pressure, muzzle values) onto a cannon's
    /// blueprint here; the player's picks go back in after.
    /// </summary>
    [HarmonyPatch(typeof(Cannon), nameof(Cannon.SyncBlueprintWithTech), typeof(CannonBlueprint), typeof(Sprocket.TechTrees.IReadOnlyTech))]
    internal static class PressureTechSyncPatch
    {
        private static void Postfix(CannonBlueprint __0)
        {
            if (__0 == null)
                return;
            Pressure.Restore(__0);
            Breech.Apply(__0);
        }
    }
}
