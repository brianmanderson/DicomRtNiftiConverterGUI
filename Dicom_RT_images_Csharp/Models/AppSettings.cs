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
        /// Global toggle: whether to export image series as image.nii.gz.
        /// </summary>
        public bool ExportImages { get; set; } = true;

        /// <summary>
        /// Global toggle: whether to include RT Struct masks in the export.
        /// </summary>
        public bool IncludeStructures { get; set; } = true;

        /// <summary>
        /// Global toggle: whether to include RT Dose in the export.
        /// </summary>
        public bool IncludeDose { get; set; } = true;

        /// <summary>
        /// When true, only ROIs matching a defined association are exported.
        /// When false, all ROIs are exported regardless of associations.
        /// </summary>
        public bool OnlyExportSpecificRois { get; set; } = false;
    }
}
