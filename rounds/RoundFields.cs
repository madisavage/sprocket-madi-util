using System;
using System.Collections.Generic;
using MadiLib;
using Properties;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;

namespace Rounds
{
    /// <summary>
    /// Sliders and readouts for a round's own fields in the cannon panel, for a
    /// <see cref="RoundDrawer"/>: each slider edits the round through
    /// <see cref="Loadout.Edit"/>, marks the vehicle changed and refreshes the readouts.
    /// </summary>
    public static class RoundFields
    {
        // Cannon blueprint, round id and the drawing plugin's key, its sliders and readouts
        private static readonly Dictionary<(IntPtr, string, string), (FloatProperty[] Fields, StringProperty[] Readouts)> sets = new();

        /// <summary>
        /// Round <paramref name="id"/>'s sliders and readouts in <paramref name="gun"/> under <paramref name="key"/>
        /// (unique per drawer, such as its round type), made by <paramref name="make"/> on first use and kept
        /// so their callbacks stay alive and the panel's redraws reuse them.
        /// </summary>
        public static (FloatProperty[] Fields, StringProperty[] Readouts) For(CannonBlueprint gun, string id, string key, Func<(FloatProperty[], StringProperty[])> make)
        {
            if (!sets.TryGetValue((gun.Pointer, id, key), out var set))
                sets[(gun.Pointer, id, key)] = set = make();
            return set;
        }

        /// <summary>Draws <paramref name="set"/>'s sliders, then its readouts.</summary>
        public static void Draw(IGUIElementDrawer drawer, (FloatProperty[] Fields, StringProperty[] Readouts) set)
        {
            foreach (FloatProperty field in set.Fields)
                drawer.Float(field);
            foreach (StringProperty readout in set.Readouts)
                drawer.Readonly(readout);
        }

        /// <summary>
        /// A slider showing <paramref name="get"/> that edits round <paramref name="id"/> in <paramref name="gun"/>
        /// as a <typeparamref name="T"/> with <paramref name="set"/>, then calls <paramref name="changed"/> and
        /// refreshes <paramref name="readouts"/>. It spans <paramref name="min"/> to <paramref name="softMax"/>
        /// when given, and typing goes up to <paramref name="max"/>; without a max, set one after.
        /// </summary>
        public static FloatProperty Field<T>(CannonBlueprint gun, string id, string name, Func<float> get, Action<T, float> set, string tooltip,
            Action changed, StringProperty[] readouts, float min, float step, float? max = null, float? softMax = null) where T : new()
        {
            var field = new FloatProperty(name, get, (Action<float>)(value =>
            {
                Loadout.Edit<T>(gun, id, round => set(round, value));
                changed();
                EditorGui.Refresh(readouts);
            }), tooltip)
            { Min = min };
            // In the order the game's own panels set them, Step last
            if (max is { } top)
                field.Max = top;
            if (softMax is { } soft)
            {
                field.SoftMin = min;
                field.SoftMax = soft;
                field.UseSoftLimits = true;
            }
            field.Step = step;
            return field;
        }
    }
}
