using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sprocket.Blueprints;

namespace MadiLib
{
    /// <summary>
    /// Settings kept as JSON in a blueprint's description, which the game saves
    /// and loads untouched but has no UI for. Plugins share one description, each
    /// under its own keys, so edits keep every other key. A description holding
    /// other text (a player's notes) is left as it is.
    /// </summary>
    public static class Descriptions
    {
        /// <summary><paramref name="text"/> as a JSON object: empty for no text, null when it holds other text.</summary>
        public static JsonObject? Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return new JsonObject();
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// <paramref name="text"/> with <paramref name="key"/> set to <paramref name="value"/> (removed for null),
        /// other keys kept; null when it holds other text than JSON, which must be left as it is.
        /// </summary>
        public static string? With(string? text, string key, JsonNode? value)
        {
            if (Parse(text) is not { } json)
                return null;
            if (value == null)
                json.Remove(key);
            else
                json[key] = value;
            return json.ToJsonString();
        }

        // Blueprint, its description and that parsed (null when it isn't JSON); one entry per blueprint read
        private static readonly Dictionary<IntPtr, (string Text, JsonObject? Json)> parsed = new();

        /// <summary>
        /// <paramref name="blueprint"/>'s description as JSON, parsed again only
        /// when it changes, as hooks read it many times a frame; null when it
        /// holds other text. Shared between plugins, so never change it: use
        /// <see cref="Read"/> or <see cref="Set"/> to edit.
        /// </summary>
        public static JsonObject? Parsed(Blueprint blueprint)
        {
            string text = blueprint.Description ?? "";
            if (parsed.TryGetValue(blueprint.Pointer, out var cached) && cached.Text == text)
                return cached.Json;
            JsonObject? json = Parse(text);
            parsed[blueprint.Pointer] = (text, json);
            return json;
        }

        /// <summary><paramref name="blueprint"/>'s description as JSON, a fresh copy to edit; null when it holds other text.</summary>
        public static JsonObject? Read(Blueprint blueprint) => Parse(blueprint.Description);

        /// <summary>Sets <paramref name="key"/> in <paramref name="blueprint"/>'s description (removed for null), other keys kept; false when it holds other text.</summary>
        public static bool Set(Blueprint blueprint, string key, JsonNode? value)
        {
            if (With(blueprint.Description, key, value) is not { } text)
                return false;
            blueprint.Description = text;
            return true;
        }
    }
}
