using System;

namespace HighExplosive
{
    /// <summary>
    /// A HEAT warhead's design: its standoff (nose length, in calibers) and
    /// its liner's cone angle (full angle, in degrees), and what they do.
    /// </summary>
    internal static class ShapedCharge
    {
        internal const float MinStandoff = 0.5f, BestStandoff = 2.5f, MaxStandoff = 6f;
        internal const float MinCone = 30f, DefaultCone = 60f, MaxCone = 120f;

        // Width of the standoff curve, in log standoff: 0.41 of peak at 0.5 calibers, 0.77 at 6
        private const float Spread = 1.2f;

        /// <summary>The share of its peak penetration a charge gets at <paramref name="standoff"/>: too close and the jet hasn't formed, too far and it has broken up.</summary>
        internal static float StandoffFactor(float standoff)
        {
            float log = MathF.Log(standoff / BestStandoff);
            return MathF.Exp(-log * log / (2f * Spread * Spread));
        }

        /// <summary>Penetration over a <see cref="DefaultCone"/> liner's: narrower cones make deeper jets.</summary>
        internal static float ConeFactor(float cone) => MathF.Sqrt(DefaultCone / cone);

        /// <summary>
        /// Jet width over a <see cref="DefaultCone"/> liner's: wider cones make
        /// fatter jets. The game's spall goes as the cube of the width.
        /// </summary>
        internal static float WidthFactor(float cone) => 1f / ConeFactor(cone);

        /// <summary>Penetration in calibers, given <paramref name="peak"/> at the best standoff with a default cone.</summary>
        internal static float Penetration(float standoff, float cone, float peak) => peak * StandoffFactor(standoff) * ConeFactor(cone);

        /// <summary>What a nose multiplies the shell's mass or drag by: each caliber past the first adds <paramref name="perCaliber"/>.</summary>
        internal static float Nose(float standoff, float perCaliber) => 1f + perCaliber * MathF.Max(standoff - 1f, 0f);

        /// <summary>
        /// The design closest to an older round's <paramref name="penetration"/>
        /// in calibers: short of the peak by a shorter standoff, then a wider
        /// cone; past it by a narrower cone, until the sliders' limits.
        /// </summary>
        internal static (float Standoff, float Cone) Fit(float penetration, float peak)
        {
            float ratio = penetration / peak;
            float standoff = ratio >= 1f ? BestStandoff
                : MathF.Max(BestStandoff * MathF.Exp(-Spread * MathF.Sqrt(-2f * MathF.Log(ratio))), MinStandoff);
            float missing = StandoffFactor(standoff) / ratio;
            return (standoff, Math.Clamp(DefaultCone * missing * missing, MinCone, MaxCone));
        }
    }
}
