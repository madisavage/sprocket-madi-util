extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HarmonyLib;
using Sprocket.Vehicles;
using Sprocket.Vehicles.PartImporting;
using Sprocket.Vehicles.Serialization;
using Object = UnityCoreModule::UnityEngine.Object;

namespace MadiUtil
{
    /// <summary>
    /// Vehicle files whose parts came from a mod that's since been removed: the
    /// game can't find the part's GUID and refuses the whole vehicle. This drops
    /// those parts (and anything mounted on them) from the file's JSON before the
    /// game reads it, so the rest of the vehicle loads. The file on disk is left
    /// alone until the player saves; a copy is kept once as <c>name.blueprint.bak</c>.
    /// </summary>
    internal static class MissingParts
    {
        /// <summary>
        /// <paramref name="json"/> without the <c>objects</c> whose <c>guid</c> isn't
        /// <paramref name="known"/>, nor their children (<c>pvuid</c>); mirror links
        /// to a dropped part are cleared. Null when nothing is dropped, the text
        /// isn't a vehicle file, or the root part itself is unknown (then the
        /// lookup, not the file, is wrong).
        /// </summary>
        internal static string? Strip(string json, Func<string, bool> known, List<string> removed)
        {
            JsonObject? file;
            try { file = JsonNode.Parse(json) as JsonObject; }
            catch (Exception) { return null; } // e.g. NaN, which System.Text.Json rejects: leave it to the game
            // Not a JSON object: not a vehicle file
            if (file == null)
                return null;
            JsonArray? objects = file["objects"] as JsonArray;
            if (objects == null)
                return null;

            // node's whole number key; -1 when it's missing
            static int Int(JsonNode? node, string key)
            {
                JsonValue? value = node?[key] as JsonValue;
                if (value == null)
                    return -1;
                if (!value.TryGetValue(out int i))
                    return -1;
                return i;
            }
            var dropped = new HashSet<int>();
            foreach (JsonNode? o in objects)
            {
                JsonValue? g = o?["guid"] as JsonValue;
                if (g == null)
                    continue;
                if (!g.TryGetValue(out string? guid))
                    continue;
                if (known(guid))
                    continue;
                // The root part itself is unknown
                if (Int(o, "pvuid") < 0)
                    return null;
                dropped.Add(Int(o, "vuid"));
                removed.Add(guid);
            }
            if (dropped.Count == 0)
                return null;
            // Children of dropped parts go too; parents may come after their children, so repeat until nothing new
            for (bool more = true; more;)
            {
                more = false;
                foreach (JsonNode? o in objects)
                    if (dropped.Contains(Int(o, "pvuid")) && dropped.Add(Int(o, "vuid")))
                        more = true;
            }
            foreach (JsonNode? o in objects.Where(o => dropped.Contains(Int(o, "vuid"))).ToList())
                objects.Remove(o);
            foreach (JsonNode? o in objects)
            {
                JsonObject? t = o?["transform"] as JsonObject;
                if (t == null)
                    continue;
                // Mirrored by a dropped part
                if (dropped.Contains(Int(t, "mirrorVuid")))
                    t["mirrorVuid"] = -1;
            }
            return file.ToJsonString();
        }
    }

    [HarmonyPatch]
    internal static class MissingPartsPatch
    {
        private static PartAssetDatabaseLoader? parts;

        private static bool Known(string guid)
        {
            if (parts == null)
                parts = Object.FindObjectOfType<PartAssetDatabaseLoader>();
            // No database found: treat every part as known, so the game behaves as stock
            Il2CppSystem.Collections.Generic.Dictionary<string, Sprocket.PartImporting.PartDefinition>? lookup = parts?.partDatabase?.partDefinitionLookup;
            if (lookup == null)
                return true;
            return lookup.ContainsKey(guid);
        }

        private static string? Stripped(string json)
        {
            var removed = new List<string>();
            string? result = MissingParts.Strip(json, Known, removed);
            if (result != null)
                Plugin.Logger.LogWarning($"Loading a vehicle without {removed.Count} part(s) the game doesn't have (a removed mod?): {string.Join(", ", removed.Distinct())}. Saving it drops them from the file.");
            return result;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(VehicleBlueprintSerializer), nameof(VehicleBlueprintSerializer.DeserializeJSON))]
        private static void Deserialize(ref string json)
        {
            string? stripped = Stripped(json);
            if (stripped != null)
                json = stripped;
        }

        /// <summary>Only for files with missing parts, in case loading from a path doesn't go through <see cref="Deserialize"/>.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(VehicleBlueprintSerializer), nameof(VehicleBlueprintSerializer.LoadFromPath))]
        private static bool Load(VehicleBlueprintSerializer __instance, string path, ref IVehicleBlueprint __result)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Couldn't read {path} to check for missing parts, the game loads it: {e.Message}");
                return true;
            }
            string? stripped = Stripped(json);
            if (stripped == null)
                return true;
            // Keep the original once, so reinstalling the mod can bring the parts back after a save drops them
            string backup = path + ".bak";
            try
            {
                if (!File.Exists(backup))
                {
                    File.Copy(path, backup);
                    Plugin.Logger.LogWarning($"Original kept as {backup}.");
                }
            }
            catch (Exception e) { Plugin.Logger.LogError($"Couldn't back up {path}: {e.Message}"); }
            __result = __instance.DeserializeJSON(stripped);
            // Loaded here without the missing parts; the game's load would fail on them
            return false;
        }
    }
}
