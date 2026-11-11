using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MadiLib
{
    /// <summary>
    /// Settings kept as <c>[tags]</c> in a part's blueprint name, such as
    /// <c>[stab 30]</c>: names save with the vehicle but don't show in game, so
    /// the setting travels with shared vehicles. Each plugin keeps its own
    /// regex for the tag's exact form, one group per number, and names its
    /// <c>word</c> (<c>stab</c>). The same rule holds for every tag: a malformed
    /// one (<c>[stab abc]</c>) counts as none, and writing a tag clears every
    /// <c>[word ...]</c>, malformed ones included.
    /// </summary>
    public static class NameTags
    {
        // Word, a regex for any tag of it, well-formed or not, with the space before it
        private static readonly Dictionary<string, Regex> anyTags = new();

        private static Regex Any(string word)
        {
            if (!anyTags.TryGetValue(word, out Regex? any))
                anyTags[word] = any = new Regex($@"\s*\[{Regex.Escape(word)}\b[^\]]*\]", RegexOptions.IgnoreCase);
            return any;
        }

        /// <summary>
        /// The numbers in <paramref name="name"/>'s <paramref name="tag"/>, one per group, each
        /// its default in <paramref name="defaults"/> when its group didn't match; null when
        /// untagged, or when the first <c>[word ...]</c> in it is malformed.
        /// </summary>
        public static float[]? Numbers(Regex tag, string word, string? name, float[] defaults)
        {
            Match match = tag.Match(name ?? "");
            if (!match.Success || Any(word).Match(name!).Value.Trim() != match.Value.Trim())
                return null;
            return defaults.Select((fallback, i) => match.Groups[i + 1].Success ? float.Parse(match.Groups[i + 1].Value, CultureInfo.InvariantCulture) : fallback).ToArray();
        }

        /// <summary>Whether <paramref name="name"/> has <paramref name="tag"/>, a tag without numbers, by the same rule as <see cref="Numbers"/>.</summary>
        public static bool Has(Regex tag, string word, string? name) => Numbers(tag, word, name, System.Array.Empty<float>()) != null;

        /// <summary><paramref name="name"/> with every <c>[word ...]</c> removed, malformed ones too, and <paramref name="newTag"/> appended; none for null.</summary>
        public static string With(string name, string word, string? newTag)
        {
            string untagged = Any(word).Replace(name, "").Trim();
            return newTag == null ? untagged : $"{untagged} {newTag}".Trim();
        }

        /// <summary><paramref name="value"/> as a tag writes it: invariant culture, up to two decimals.</summary>
        public static string Format(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
