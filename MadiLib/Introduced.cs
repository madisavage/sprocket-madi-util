using BepInEx.Configuration;
using HarmonyLib;
using Sprocket.VehicleDesigner;

namespace MadiLib
{
    /// <summary>
    /// Limits designer choices (round types, and later ATGMs, rangefinders,
    /// active protection) to the vehicle's tech year. Only the designer
    /// checks it: a vehicle already holding something too new keeps it, and
    /// battles never check, so loading an old file loses nothing.
    /// </summary>
    public static class Introduced
    {
        // The designer, as of its last update; null before one runs
        private static VehicleDesignerCore? designer;

        /// <summary>The vehicle's tech year in the designer, or null before the designer has run.</summary>
        public static int? Year => designer?.TechnologyDate.Year;

        /// <summary>
        /// Whether something introduced in <paramref name="year"/> can be
        /// picked: always with MadiLib's <b>Historical availability</b> off or
        /// outside the designer.
        /// </summary>
        public static bool Allowed(float year) => Allowed(year, Year);

        internal static bool Allowed(float year, int? now) => !Plugin.Historical.Value || now is not int y || y >= year;

        /// <summary>The note shown on something picked before <paramref name="year"/>.</summary>
        public static string Note(float year) => $"Not available until {year:0}";

        /// <summary>
        /// Binds an "Introduced" year setting under <paramref name="section"/>,
        /// for <see cref="Allowed(float)"/>; add it to the plugin's Settings section.
        /// </summary>
        /// <param name="config">The plugin's config file.</param>
        /// <param name="section">The config section, such as "HEAT".</param>
        /// <param name="what">What the year is for, as in "the first year HEAT rounds can be picked".</param>
        /// <param name="year">The default year.</param>
        /// <param name="key">The config key, for a plugin with several years in one section.</param>
        public static ConfigEntry<float> Bind(ConfigFile config, string section, string what, float year, string key = "Introduced") =>
            config.Bind(section, key, year, new ConfigDescription(
                $"The first year {what} can be picked in the designer, with MadiLib's Historical availability on. Vehicles that already have them keep them.",
                new AcceptableValueRange<float>(1900f, 2100f), new Step(1f)));

        [HarmonyPatch(typeof(VehicleDesignerCore), nameof(VehicleDesignerCore.EditorUpdate))]
        private static class DesignerPatch
        {
            private static void Postfix(VehicleDesignerCore __instance) => designer = __instance;
        }
    }
}
