extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using Properties;
using Sprocket.UI;
using IntAction = UnityCoreModule::UnityEngine.Events.UnityAction<int>;

namespace MadiLib
{
    /// <summary>
    /// Controls for the game's editor panels (a designer editor's
    /// <c>OnGUI</c>, an overlay config's <c>Draw</c>), which the game builds
    /// once per selection rather than every frame.
    /// </summary>
    public static class EditorGui
    {
        // Section id, expanded; one Key:Value pair per section drawn
        private static readonly Dictionary<string, bool> sections = new();

        /// <summary>
        /// Draws a collapsible section header, like the game's own panel
        /// sections. Draw the section's contents only when it returns true.
        /// Sections start collapsed and stay as left for the session.
        /// </summary>
        /// <param name="drawer">The panel being drawn.</param>
        /// <param name="id">Identifies the section across redraws; unique per panel.</param>
        /// <param name="header">The header text.</param>
        /// <param name="redraw">
        /// Asks the panel to build again, such as the editor's
        /// <c>RequestRedraw</c>, or setting an overlay config's
        /// <c>GUIRedrawRequired</c>. Called when the header is clicked.
        /// </param>
        /// <returns>Whether the section is expanded.</returns>
        public static bool Section(IGUIElementDrawer drawer, string id, string header, Action redraw)
        {
            sections.TryGetValue(id, out bool shown);
            drawer.HeaderBar(header, shown, (Action<bool>)(value =>
            {
                sections[id] = value;
                redraw();
            }));
            return shown;
        }

        /// <summary>Draws a dropdown of <paramref name="options"/>.</summary>
        /// <param name="drawer">The panel being drawn.</param>
        /// <param name="label">The label beside the dropdown.</param>
        /// <param name="options">The choices, in order.</param>
        /// <param name="selected">Index of the chosen option; -1 for none.</param>
        /// <param name="onSelect">Called with the index the player picks.</param>
        /// <param name="tooltip">Shown on hovering the label.</param>
        public static void Dropdown(IGUIElementDrawer drawer, string label, IReadOnlyList<string> options, int selected, Action<int> onSelect, string tooltip = "")
        {
            var names = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (string option in options)
                names.Add(option);
            drawer.Dropdown(
                label,
                names.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(),
                selected,
                (IntAction)onSelect,
                tooltip);
        }

        /// <summary>
        /// A read-only row showing <paramref name="get"/>. Draw it with
        /// <c>drawer.Readonly</c>, cache it like any property, and pass it to
        /// <see cref="Refresh"/> when its value may have changed: the panel
        /// isn't rebuilt to update it.
        /// </summary>
        /// <param name="name">The row label.</param>
        /// <param name="get">The text shown, read each time the row refreshes.</param>
        /// <param name="description">Shown on hovering the label.</param>
        public static StringProperty Readout(string name, Func<string> get, string description = "") =>
            new(name, get, (Action<string>)(_ => { }), description);

        /// <summary>Tells the panel to show the current values of <paramref name="properties"/>.</summary>
        public static void Refresh(IEnumerable<Property> properties)
        {
            foreach (Property property in properties)
                property.OnPropertyChanged(PropertyChangeType.Value);
        }
    }
}
