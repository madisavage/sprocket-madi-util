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
            if (read.TryGetValue(directory, out var files))
                return files;
            files = new();
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
            return read[directory] = files;
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
            foreach (var (path, root) in Read(directory))
                if (root["properties"] is JsonObject p && p[key] is JsonValue v && v.TryGetValue(out double flag) && flag != 0
                    && p["spallFactor"] is JsonValue s && p["rhaFactor"] is JsonValue r && p["density"] is JsonValue d)
                    flagged[(s.GetValue<float>(), r.GetValue<float>(), d.GetValue<float>())] = (Path.GetFileNameWithoutExtension(path), (float)flag);
            return flagged;
        }

        /// <summary><paramref name="node"/> as a string, or null when it isn't one.</summary>
        public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }
}
