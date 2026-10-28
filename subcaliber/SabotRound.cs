namespace Subcaliber
{
    /// <summary>A sabot round, as stored in the vehicle file through <see cref="Rounds.Loadout"/>.</summary>
    internal sealed class SabotRound
    {
        /// <summary>Its round type in <see cref="Rounds.Loadout"/>.</summary>
        internal const string Type = "sabot";

        /// <summary>Penetrator diameter in mm; 0 fires full-bore.</summary>
        public float Diameter { get; set; }

        /// <summary>Penetrator length in calibers of its own diameter.</summary>
        public float Length { get; set; } = 3f;

        /// <summary>Penetrator material, from madi.subcaliber.materials.json.</summary>
        public string Material { get; set; } = "Steel";

        /// <summary>Fraction of the penetrator's length the sabot covers.</summary>
        public float SabotCoverage { get; set; } = 1f;

        /// <summary>Sabot density relative to steel.</summary>
        public float SabotDensity { get; set; } = 0.35f;
    }
}
