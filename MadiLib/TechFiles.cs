using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;

namespace MadiLib
{
    /// <summary>
    /// The game's tech files (eras' cannons, engines, armour materials...), read
    /// once at launch and shared, so plugins reading the same folder don't each
    /// parse it again.
    /// </summary>
    public static class TechFiles
    {
        /// <summary>The game's Technology folder.</summary>
        public static string Folder => Path.Combine(Paths.GameRootPath, "Sprocket_Data", "StreamingAssets", "Technology");

        // Folder, its files
        private static readonly Dictionary<string, List<(string Path, JsonObject Root)>> read = new();

        /// <summary>
        /// Each JSON file in <paramref name="directory"/> whose root is an object,
        /// parsed once per folder, as the game reads them (trailing commas and
        /// comments allowed). Unreadable files are left out. Shared, so never change them.
        /// </summary>
        public static IReadOnlyList<(string Path, JsonObject Root)> Read(string directory)
        {
            if (read.TryGetValue(directory, out List<(string Path, JsonObject Root)>? files))
                return files;
            files = new List<(string Path, JsonObject Root)>();
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path), documentOptions: options) is JsonObject root)
                        files.Add((path, root));
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    // No logger in unit tests
                    Plugin.Logger?.LogWarning($"Skipped tech file {Path.GetFileName(path)}, it can't be read: {e.Message}");
                }
            }
            read[directory] = files;
            return files;
        }

        /// <summary>
        /// The armour materials whose <c>properties</c> set our own <paramref name="key"/>
        /// (e.g. <c>"nera": 150</c>) to a nonzero number, with their file name and that number, keyed as the penetration sim
        /// knows a plate: by its spall factor, RHA factor and density, as it carries no tech ID.
        /// </summary>
        /// <remarks>shortcut: two materials with the same three values would both match; every file in the game's folder is unique today, so check again if a match is ever wrong.</remarks>
        public static Dictionary<(float Spall, float Rha, float Density), (string Name, float Value)> Flagged(string directory, string key)
        {
            var flagged = new Dictionary<(float, float, float), (string, float)>();
            foreach ((string path, JsonObject root) in Read(directory))
            {
                JsonObject? properties = root["properties"] as JsonObject;
                if (properties == null)
                    continue;
                JsonValue? flagValue = properties[key] as JsonValue;
                if (flagValue == null)
                    continue;
                if (!flagValue.TryGetValue(out double flag))
                    continue;
                // Zero: the material doesn't have it
                if (flag == 0)
                    continue;
                JsonValue? spall = properties["spallFactor"] as JsonValue;
                if (spall == null)
                    continue;
                JsonValue? rha = properties["rhaFactor"] as JsonValue;
                if (rha == null)
                    continue;
                JsonValue? density = properties["density"] as JsonValue;
                if (density == null)
                    continue;
                (float, float, float) plate = (spall.GetValue<float>(), rha.GetValue<float>(), density.GetValue<float>());
                flagged[plate] = (Path.GetFileNameWithoutExtension(path), (float)flag);
            }
            return flagged;
        }

        /// <summary><paramref name="node"/> as a string, or null when it isn't one.</summary>
        public static string? Text(JsonNode? node)
        {
            JsonValue? value = node as JsonValue;
            if (value == null)
                return null;
            if (!value.TryGetValue(out string? text))
                return null;
            return text;
        }
    }
}
