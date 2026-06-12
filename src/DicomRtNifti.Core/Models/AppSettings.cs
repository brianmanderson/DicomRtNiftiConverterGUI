using System;
using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
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

        /// <summary>
        /// When true, exports use anonymized integer folder IDs and generate a CSV manifest.
        /// </summary>
        public bool AnonymizeExport { get; set; } = false;

        /// <summary>
        /// Salt used for deterministic hashing during anonymized exports.
        /// Same salt + same input always produces the same hash.
        /// </summary>
        public string HashSalt { get; set; } = "DicomToNifti";

        /// <summary>
        /// When true, all exports (image, dose, masks) are resampled to the user-specified output spacing.
        /// </summary>
        public bool SpecifyOutputSpacing { get; set; } = false;

        /// <summary>
        /// Output voxel spacing in mm along the X axis (only used when SpecifyOutputSpacing=true).
        /// </summary>
        public double OutputSpacingX { get; set; } = 1.0;

        /// <summary>
        /// Output voxel spacing in mm along the Y axis (only used when SpecifyOutputSpacing=true).
        /// </summary>
        public double OutputSpacingY { get; set; } = 1.0;

        /// <summary>
        /// Output voxel spacing in mm along the Z axis (only used when SpecifyOutputSpacing=true).
        /// </summary>
        public double OutputSpacingZ { get; set; } = 1.0;

        /// <summary>
        /// When true, a metadata.json with the user-selected DICOM tags is written into each
        /// series' export folder during a Convert export.
        /// </summary>
        public bool ExportDicomMetadata { get; set; } = false;

        /// <summary>
        /// Legacy flat list of metadata-tag keywords. Superseded by the per-section lists below;
        /// retained only so old settings files still deserialize. On load,
        /// <see cref="Services.SettingsService.MigrateMetadataTagKeywords"/> moves any contents into
        /// <see cref="MetadataImageTagKeywords"/> and clears this; it then persists empty.
        /// </summary>
        public List<string> MetadataTagKeywords { get; set; } = new List<string>();

        /// <summary>
        /// Image-tab metadata selections written to the "ImageAttributes" section of metadata.json.
        /// DICOM keywords (e.g. "PatientName") and/or "@..." computed keys (e.g. "@VoxelSize").
        /// </summary>
        public List<string> MetadataImageTagKeywords { get; set; } = new List<string>();

        /// <summary>
        /// Structures-tab metadata selections written to the "StructureAttributes" section.
        /// </summary>
        public List<string> MetadataStructureTagKeywords { get; set; } = new List<string>();

        /// <summary>
        /// Dose-tab metadata selections written to the "DoseAttributes" section.
        /// </summary>
        public List<string> MetadataDoseTagKeywords { get; set; } = new List<string>();
    }
}
