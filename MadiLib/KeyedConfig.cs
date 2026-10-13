using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace MadiLib
{
    /// <summary>
    /// Settings stored per key in one config section, such as a value per
    /// shell type keyed by its GUID. A key reads as the fallback until set,
    /// and only set keys are written to the file.
    /// </summary>
    /// <remarks>
    /// BepInEx hides values saved last session until something binds their
    /// key, so the constructor binds every key already in the section.
    /// Create it in <c>Load</c>, before anything reads it.
    /// </remarks>
    public sealed class KeyedConfig<T>
    {
        private readonly ConfigFile file;
        private readonly string section;
        private readonly T fallback;
        private readonly string description;

        /// <param name="file">The plugin's config, usually its <c>Config</c>.</param>
        /// <param name="section">The section holding one entry per key.</param>
        /// <param name="fallback">The value of a key that was never set.</param>
        /// <param name="description">Written to the file above each entry.</param>
        public KeyedConfig(ConfigFile file, string section, T fallback, string description)
        {
            this.file = file;
            this.section = section;
            this.fallback = fallback;
            this.description = description;
            var saved = (Dictionary<ConfigDefinition, string>)AccessTools.Property(typeof(ConfigFile), "OrphanedEntries").GetValue(file)!;
            foreach (ConfigDefinition key in saved.Keys.Where(d => d.Section == section).ToList())
                file.Bind(key, fallback, new ConfigDescription(description));
        }

        /// <summary>The value for <paramref name="key"/>; setting it saves it.</summary>
        public T this[string key]
        {
            get => file.TryGetEntry(section, key, out ConfigEntry<T> entry) ? entry.Value : fallback;
            set => file.Bind(section, key, fallback, description).Value = value;
        }
    }
}
