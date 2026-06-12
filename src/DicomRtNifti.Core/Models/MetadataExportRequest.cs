using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// Inputs for one series' <c>metadata.json</c> export. Carries the three per-modality keyword
    /// lists (image / structure / dose, each possibly containing "@..." computed pseudo-keywords)
    /// and the source files they read from: image tags from the first slice in
    /// <see cref="ImageFilePaths"/>, structure tags from <see cref="StructureFilePath"/>, dose tags
    /// from <see cref="DoseFilePath"/>. A section is written only when its keyword list is non-empty;
    /// when its source file is missing every value in that section is null.
    /// </summary>
    public class MetadataExportRequest
    {
        /// <summary>The series' image slice paths; the first is the header source for image tags.</summary>
        public IReadOnlyList<string> ImageFilePaths { get; set; }

        /// <summary>Linked RTSTRUCT file, or null when the series has none.</summary>
        public string StructureFilePath { get; set; }

        /// <summary>Linked RTDOSE file, or null when the series has none.</summary>
        public string DoseFilePath { get; set; }

        /// <summary>Image-tab selections (DICOM keywords and/or "@..." computed keys).</summary>
        public IReadOnlyList<string> ImageKeywords { get; set; }

        /// <summary>Structures-tab selections (DICOM keywords and/or "@..." computed keys).</summary>
        public IReadOnlyList<string> StructureKeywords { get; set; }

        /// <summary>Dose-tab selections (DICOM keywords and/or "@..." computed keys).</summary>
        public IReadOnlyList<string> DoseKeywords { get; set; }

        /// <summary>
        /// Optional export-accurate voxel spacing [x, y, z] for the "@VoxelSize" computed value;
        /// when null it is derived from the first slice's PixelSpacing/SliceThickness.
        /// </summary>
        public double[] ImageVoxelSpacing { get; set; }
    }
}
