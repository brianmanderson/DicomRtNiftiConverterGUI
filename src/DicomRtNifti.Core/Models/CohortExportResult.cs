using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// What a cohort export actually produced. Paths are relative to the cohort root and use
    /// forward slashes; only the root itself is absolute. That keeps the serialized form free of
    /// escaped Windows separators and lets a consumer rejoin them on any platform.
    /// </summary>
    public class CohortExportResult
    {
        /// <summary>Absolute path of the cohort root.</summary>
        public string OutputRoot { get; set; } = "";

        /// <summary>Manifest filename, relative to the root.</summary>
        public string ManifestCsv { get; set; } = "";

        /// <summary>True when identifiers in output paths, the manifest and this result are hashed.</summary>
        public bool Anonymized { get; set; }

        /// <summary>Anonymization key filename relative to the root, or null when not anonymizing.</summary>
        public string AnonymizationKey { get; set; }

        /// <summary>The spacing every series was resampled to, or null when native grids were kept.</summary>
        public double[] OutputSpacing { get; set; }

        /// <summary>Whether ROI volumes were computed (they require a rasterization pass).</summary>
        public bool VolumesComputed { get; set; }

        /// <summary>Union of ROI column names across all exported series, in first-seen order.</summary>
        public List<string> RoiColumns { get; set; } = new List<string>();

        public List<SeriesExportResult> Series { get; set; } = new List<SeriesExportResult>();

        /// <summary>Series that were planned but failed during conversion.</summary>
        public List<CohortError> Errors { get; set; } = new List<CohortError>();

        /// <summary>Series that were excluded at planning time, each with a reason.</summary>
        public List<SkippedSeries> Skipped { get; set; } = new List<SkippedSeries>();

        public int SucceededCount { get; set; }
        public int FailedCount { get; set; }
    }

    /// <summary>One exported series.</summary>
    public class SeriesExportResult
    {
        public string PatientId { get; set; } = "";
        public string StudyUid { get; set; } = "";
        public string SeriesUid { get; set; } = "";

        /// <summary>Output directory relative to the cohort root.</summary>
        public string OutputDir { get; set; } = "";

        /// <summary>image.nii.gz relative to the cohort root, or null when images were not exported.</summary>
        public string Image { get; set; }

        /// <summary>Voxel spacing [x, y, z] of what was written.</summary>
        public double[] Spacing { get; set; }

        public List<MaskExportResult> Masks { get; set; } = new List<MaskExportResult>();

        /// <summary>Dose volumes relative to the cohort root.</summary>
        public List<string> Doses { get; set; } = new List<string>();

        /// <summary>metadata.json relative to the cohort root, or null when no tags were requested.</summary>
        public string Metadata { get; set; }

        /// <summary>How the structure set was matched to this image series; a QC signal.</summary>
        public string StructLinkRule { get; set; }
    }

    /// <summary>One exported ROI mask.</summary>
    public class MaskExportResult
    {
        /// <summary>Exported name — the canonical name when an association matched, else the raw ROI name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Mask volume in cc, or the missing sentinel when volumes were not computed.</summary>
        public double VolumeCc { get; set; }

        /// <summary>Mask file relative to the cohort root.</summary>
        public string File { get; set; } = "";
    }

    /// <summary>A series that failed to convert.</summary>
    public class CohortError
    {
        public string PatientId { get; set; } = "";
        public string SeriesUid { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
