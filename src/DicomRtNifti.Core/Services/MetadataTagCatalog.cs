using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Catalog that drives the three-tab metadata-tag picker (Images / Structures / Dose). It
    /// supplies, per tab, a short curated list of clinically relevant DICOM keywords plus a set
    /// of computed (derived) values, and it owns the single rule that turns a keyword into the
    /// friendly JSON key written to metadata.json.
    ///
    /// Computed values are pseudo-keywords prefixed with <see cref="ComputedPrefix"/> ("@"); the
    /// '@' cannot appear in a DICOM dictionary keyword, so they coexist with raw keywords in the
    /// same selection lists, settings, and serialization without collision.
    /// </summary>
    public static class MetadataTagCatalog
    {
        /// <summary>Prefix marking a computed pseudo-keyword (e.g. "@VoxelSize").</summary>
        public const string ComputedPrefix = "@";

        // Computed-value keys. Kept as consts so the extractor's per-section resolvers and the
        // option lists below stay in lock-step.
        public const string VoxelSizeKey = "@VoxelSize";
        public const string ImageDimensionsKey = "@ImageDimensions";
        public const string RoiNamesKey = "@RoiNames";
        public const string RoiCountKey = "@RoiCount";
        public const string MaxDoseKey = "@MaxDose";
        public const string DoseVoxelSizeKey = "@DoseVoxelSize";

        /// <summary>True if <paramref name="keyword"/> is a computed pseudo-keyword.</summary>
        public static bool IsComputedKey(string keyword)
            => !string.IsNullOrEmpty(keyword) && keyword.StartsWith(ComputedPrefix, StringComparison.Ordinal);

        // Splits a PascalCase keyword into space-separated words, keeping acronyms intact:
        //   PatientName    -> "Patient Name"
        //   PatientID      -> "Patient ID"
        //   SOPInstanceUID -> "SOP Instance UID"
        //   ROIName        -> "ROI Name"
        // Boundaries: lower/digit -> upper, and upper -> upper-then-lower (acronym to word).
        private static readonly Regex PascalCaseBoundary =
            new Regex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        /// <summary>
        /// The friendly JSON key for a keyword. Computed pseudo-keywords resolve via their
        /// <see cref="ComputedMetadataOption.FriendlyName"/>; raw DICOM keywords are split from
        /// PascalCase (acronym-aware). Returns the input unchanged if it is null/empty.
        /// </summary>
        public static string FriendlyName(string keywordOrComputedKey)
        {
            if (string.IsNullOrEmpty(keywordOrComputedKey))
                return keywordOrComputedKey;

            if (IsComputedKey(keywordOrComputedKey))
            {
                if (ComputedByKey.TryGetValue(keywordOrComputedKey, out var option))
                    return option.FriendlyName;
                // Unknown computed key: strip the prefix and split whatever remains.
                return PascalCaseBoundary.Replace(keywordOrComputedKey.Substring(ComputedPrefix.Length), " ");
            }

            return PascalCaseBoundary.Replace(keywordOrComputedKey, " ");
        }

        // --- Computed options, per tab ---------------------------------------------------------

        public static IReadOnlyList<ComputedMetadataOption> ImageComputedOptions { get; } = new[]
        {
            new ComputedMetadataOption
            {
                Key = VoxelSizeKey, FriendlyName = "Voxel Size",
                Description = "Output voxel spacing in mm [x, y, z]"
            },
            new ComputedMetadataOption
            {
                Key = ImageDimensionsKey, FriendlyName = "Image Dimensions",
                Description = "Image grid size [columns, rows, slices]"
            },
        };

        public static IReadOnlyList<ComputedMetadataOption> StructureComputedOptions { get; } = new[]
        {
            new ComputedMetadataOption
            {
                Key = RoiNamesKey, FriendlyName = "ROI Names",
                Description = "Names of every ROI in the structure set"
            },
            new ComputedMetadataOption
            {
                Key = RoiCountKey, FriendlyName = "Number of ROIs",
                Description = "Count of ROIs in the structure set"
            },
        };

        public static IReadOnlyList<ComputedMetadataOption> DoseComputedOptions { get; } = new[]
        {
            new ComputedMetadataOption
            {
                Key = MaxDoseKey, FriendlyName = "Max Dose",
                Description = "Maximum dose value (grid scaling applied)"
            },
            new ComputedMetadataOption
            {
                Key = DoseVoxelSizeKey, FriendlyName = "Dose Grid Voxel Size",
                Description = "Dose grid voxel spacing in mm [x, y, z]"
            },
        };

        private static readonly Dictionary<string, ComputedMetadataOption> ComputedByKey = BuildComputedByKey();

        private static Dictionary<string, ComputedMetadataOption> BuildComputedByKey()
        {
            var map = new Dictionary<string, ComputedMetadataOption>(StringComparer.Ordinal);
            foreach (var option in ImageComputedOptions) map[option.Key] = option;
            foreach (var option in StructureComputedOptions) map[option.Key] = option;
            foreach (var option in DoseComputedOptions) map[option.Key] = option;
            return map;
        }

        // --- Curated keyword lists, per tab ----------------------------------------------------
        // Every keyword here must exist in DicomMetadataExtractor.GetSelectableTags()
        // (enforced by MetadataTagCatalogTests). Ordered roughly identity -> study/series ->
        // acquisition so the curated checklist reads sensibly.

        public static IReadOnlyList<string> CuratedImageKeywords { get; } = new[]
        {
            "PatientName", "PatientID", "PatientBirthDate", "PatientSex", "PatientAge", "PatientWeight",
            "StudyDate", "StudyDescription", "StudyInstanceUID",
            "SeriesDate", "SeriesDescription", "SeriesInstanceUID",
            "Modality", "FrameOfReferenceUID", "BodyPartExamined", "PatientPosition", "ProtocolName",
            "SliceThickness", "PixelSpacing", "Rows", "Columns", "ImageOrientationPatient",
            "Manufacturer", "ManufacturerModelName", "InstitutionName",
            "KVP", "ConvolutionKernel", "ContrastBolusAgent", "RescaleSlope", "RescaleIntercept",
            "MagneticFieldStrength", "RepetitionTime", "EchoTime", "FlipAngle",
            "Units", "DecayCorrection"
        };

        public static IReadOnlyList<string> CuratedStructureKeywords { get; } = new[]
        {
            "StructureSetLabel", "StructureSetName", "StructureSetDescription",
            "StructureSetDate", "StructureSetTime",
            "SeriesDescription", "SeriesInstanceUID", "StudyInstanceUID", "SOPInstanceUID",
            "Modality", "Manufacturer", "ManufacturerModelName", "SoftwareVersions",
            "OperatorsName", "ReviewDate", "ReviewTime", "ReviewerName", "ApprovalStatus",
            "InstanceCreationDate", "PatientName", "PatientID"
        };

        public static IReadOnlyList<string> CuratedDoseKeywords { get; } = new[]
        {
            "DoseUnits", "DoseType", "DoseSummationType", "DoseComment", "DoseGridScaling",
            "TissueHeterogeneityCorrection", "GridFrameOffsetVector", "NumberOfFrames",
            "Rows", "Columns", "PixelSpacing", "SliceThickness",
            "SeriesDescription", "SeriesInstanceUID", "StudyInstanceUID", "SOPInstanceUID",
            "Modality", "Manufacturer", "ManufacturerModelName", "SoftwareVersions",
            "InstanceCreationDate", "FrameOfReferenceUID", "PatientName", "PatientID"
        };
    }
}
