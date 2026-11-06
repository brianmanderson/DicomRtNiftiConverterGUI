using System;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// How much a cohort analysis reads, priced by what it costs beyond the scan (web plan §5.1).
    /// Each tier includes the ones before it.
    /// </summary>
    public enum AnalysisTier
    {
        /// <summary>The scan's own model plus values read from the headers it already opens. Costs nothing more.</summary>
        Header = 0,

        /// <summary>Adds a second, full parse of each linked structure set's contours and of each linked dose.</summary>
        Contours = 1,

        /// <summary>Adds a decode and rasterization of every series, the cost of --cohort-manifest. Not yet available.</summary>
        Raster = 2,
    }

    /// <summary>The tier names as the command line and the document spell them.</summary>
    public static class AnalysisTierNames
    {
        public const string Header = "header";
        public const string Contours = "contours";
        public const string Raster = "raster";

        public static string Of(AnalysisTier tier)
        {
            switch (tier)
            {
                case AnalysisTier.Header: return Header;
                case AnalysisTier.Contours: return Contours;
                case AnalysisTier.Raster: return Raster;
                default: throw new ArgumentOutOfRangeException(nameof(tier));
            }
        }

        /// <summary>Parses a tier name, ignoring case; false for anything else.</summary>
        public static bool TryParse(string name, out AnalysisTier tier)
        {
            foreach (AnalysisTier candidate in Enum.GetValues(typeof(AnalysisTier)))
            {
                if (string.Equals(name, Of(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    tier = candidate;
                    return true;
                }
            }
            tier = AnalysisTier.Header;
            return false;
        }

        /// <summary>
        /// True for the tiers this build computes. The raster tier arrives with brief W4-K; until
        /// then it is refused before any scan runs.
        /// </summary>
        public static bool IsAvailable(AnalysisTier tier) => tier <= AnalysisTier.Contours;
    }
}
