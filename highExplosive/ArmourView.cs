using System;
using System.Collections.Generic;
using MadiLib;
using Properties;
using Rounds;
using Sprocket.DamageModelling;
using Sprocket.UI;

namespace HighExplosive
{
    /// <summary>
    /// Adds <b>Simulate as</b> to the armour view's penetration simulator: its
    /// shot hits as the game's shell, an HE shell (normalization and spall),
    /// a HEAT jet, at the penetration the panel asks for and the section's own
    /// <b>Cone angle</b>, or an APHE shell with the section's own <b>Filler</b>
    /// and <b>Fuze delay</b>.
    /// </summary>
    internal static class ArmourView
    {
        private static readonly List<string> modes = new() { "Shell", "HE", "HEAT", "APHE", "HESH" };
        private static int mode;
        // The APHE shot's round, set from the section
        private static readonly ApheRound aphe = new();
        private static FloatProperty? filler, fuze, cone;
        // The HEAT shot's cone, set from the section
        private static float heatCone = ShapedCharge.DefaultCone;

        /// <summary>Draws the section, from <see cref="Impacts.ArmourPanel"/>.</summary>
        internal static void Draw(IGUIElementDrawer drawer, Action redraw)
        {
            if (!EditorGui.Section(drawer, "highexplosive.armour", "Explosive rounds", redraw))
                return;
            EditorGui.Dropdown(drawer, "Simulate as", modes, mode, i =>
            {
                mode = i;
                Impacts.Resimulate();
            }, "HE: the shot turns toward the plate and spalls as a full-bore HE shell's. HEAT: the shot is a HEAT jet, at the panel's penetration and the cone angle below. APHE: the shot bursts as an APHE round with the filler and fuze delay below. HESH: as HE, with HESH's normalization.");
            if (mode == 2)
            {
                cone ??= new FloatProperty(
                    "Cone angle",
                    (Func<float>)(() => heatCone),
                    (Action<float>)(value =>
                    {
                        heatCone = value;
                        Impacts.Resimulate();
                    }),
                    "The HEAT shot's liner angle in degrees, which sets its jet's width and spall. Its penetration is the panel's.")
                {
                    Min = ShapedCharge.MinCone,
                    Max = ShapedCharge.MaxCone,
                    Step = 5f
                };
                drawer.Float(cone);
            }
            if (mode != 3)
                return;
            filler ??= new FloatProperty(
                "Filler (g)",
                (Func<float>)(() => aphe.Filler * 1000f),
                (Action<float>)(value =>
                {
                    aphe.Filler = value / 1000f;
                    Impacts.Resimulate();
                }),
                "The APHE shot's explosive in grams; at most 10% of the shot's mass, whatever the slider.")
            {
                Min = 5f,
                Max = 2000f,
                Step = 5f
            };
            fuze ??= new FloatProperty(
                "Fuze delay",
                (Func<float>)(() => aphe.Fuze),
                (Action<float>)(value =>
                {
                    aphe.Fuze = value;
                    Impacts.Resimulate();
                }),
                "Metres the APHE shot goes past where it first enters a plate before it bursts.")
            {
                Min = 0.05f,
                Max = 3f,
                Step = 0.05f
            };
            drawer.Float(filler);
            drawer.Float(fuze);
        }

        internal static void Simulate(ref PenetratorInfo penetrator)
        {
            if (mode == 1)
            {
                Plugin.Blast(ref penetrator, 1f, Plugin.Normalization.Value);
                Fuze.StartAtExit();
                AirBlast.Start(penetrator, HeKind.He);
            }
            else if (mode == 4)
            {
                Plugin.Blast(ref penetrator, 1f, Plugin.HeshNormalization.Value);
                Fuze.StartAtExit();
                AirBlast.Start(penetrator, HeKind.Hesh);
            }
            else if (mode == 2)
                Heat.Jet(ref penetrator, Impacts.Penetration(penetrator), heatCone);
            else if (mode == 3)
                Aphe.Shoot(ref penetrator, aphe);
        }
    }
}
