using System;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Application settings persisted to JSON.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Default directory for NIfTI output files.
        /// </summary>
        public string DefaultOutputDirectory { get; set; } = "";

        /// <summary>
        /// Whether to open the output folder in Explorer after conversion completes.
        /// </summary>
        public bool AutoOpenAfterConversion { get; set; } = false;

        /// <summary>
        /// Whether to export ROIs that do not match any association, using their original names.
        /// </summary>
        public bool ExportUnmatchedRois { get; set; } = true;
    }
}
