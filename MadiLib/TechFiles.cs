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
                }
            }
            return read[directory] = files;
        }

        /// <summary><paramref name="node"/> as a string, or null when it isn't one.</summary>
        public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }
}
