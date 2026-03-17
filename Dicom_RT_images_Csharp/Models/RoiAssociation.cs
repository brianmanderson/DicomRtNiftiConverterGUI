using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Maps a canonical ROI name to a set of aliases for matching against DICOM structure names.
    /// </summary>
    public class RoiAssociation
    {
        /// <summary>
        /// The standardized name used for output file naming.
        /// </summary>
        public string CanonicalName { get; set; } = "";

        /// <summary>
        /// Alternative names that should map to this canonical name (case-insensitive matching).
        /// </summary>
        public List<string> Aliases { get; set; } = new List<string>();

        /// <summary>
        /// Arbitrary key-value metadata (e.g. color, priority, is_oar, TG-263 code).
        /// </summary>
        public Dictionary<string, string> ExtraAttributes { get; set; } = new Dictionary<string, string>();
    }
}
