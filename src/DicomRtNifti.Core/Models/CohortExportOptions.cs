using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// Everything needed to turn a scanned DICOM tree into an exported cohort. Mirrors the
    /// toggles the desktop app exposes across its export-options, ROI-association, output-spacing
    /// and metadata-tag dialogs, so the CLI and the GUI can drive the same conversion.
    /// </summary>
    public class CohortExportOptions
    {
        /// <summary>Root folder to scan recursively for DICOM.</summary>
        public string InputRoot { get; set; }

        /// <summary>Root folder to write the exported cohort into.</summary>
        public string OutputRoot { get; set; }

        // ---- Series selection -------------------------------------------------------------

        /// <summary>
        /// When non-empty, only these PatientIDs are exported. Matched case-insensitively
        /// against the source PatientID, before any anonymization.
        /// </summary>
        public List<string> PatientIds { get; set; } = new List<string>();

        /// <summary>
        /// When set, only image series whose SeriesDescription contains this substring
        /// (case-insensitive) are exported.
        ///
        /// Needed because a study can hold several series of the same modality: a planning CT
        /// alongside CBCTs resampled onto its grid all report Modality=CT, and without a filter
        /// every one of them would be converted.
        /// </summary>
        public string SeriesDescriptionFilter { get; set; }

        /// <summary>
        /// When set, only image series carrying a linked structure set whose SeriesDescription
        /// contains this substring (case-insensitive) are exported, and that structure set is the
        /// one used.
        ///
        /// This is usually the sharper of the two selectors. Sibling image series in a study are
        /// often near-identical — a CBCT resampled onto the planning grid has the same modality,
        /// frame of reference, spacing *and* slice count as the planning CT it was aligned to —
        /// while the structure sets drawn on them are named for what they are.
        /// </summary>
        public string StructDescriptionFilter { get; set; }

        /// <summary>
        /// When true, keep only the image series with the most slices in each study. A blunt
        /// stand-in for "the planning CT" when descriptions are absent or inconsistent. Applied
        /// after the description filters. Ties break on SeriesInstanceUID so the choice is
        /// reproducible — but note that reproducible is not the same as correct: where sibling
        /// series are resampled onto a shared grid the slice counts tie exactly, and this rule
        /// then picks arbitrarily. Prefer <see cref="StructDescriptionFilter"/> when the
        /// structure sets are distinguishable.
        /// </summary>
        public bool PreferLargestSeries { get; set; }

        /// <summary>Require a linked RTSTRUCT; series without one are skipped.</summary>
        public bool RequireStructures { get; set; }

        /// <summary>Require at least one linked RTDOSE; series without one are skipped.</summary>
        public bool RequireDose { get; set; }

        // ---- What to write ----------------------------------------------------------------

        public bool ExportImages { get; set; } = true;
        public bool ExportStructures { get; set; } = true;
        public bool ExportDoses { get; set; } = true;

        // ---- ROI handling -----------------------------------------------------------------

        /// <summary>Canonical-name to alias mappings applied to ROI names at export time.</summary>
        public List<RoiAssociation> Associations { get; set; } = new List<RoiAssociation>();

        /// <summary>
        /// When true, only ROIs matching an association are exported. When false (the default)
        /// unmatched ROIs are exported under their raw DICOM names.
        /// </summary>
        public bool OnlyAssociatedRois { get; set; }

        // ---- Geometry ---------------------------------------------------------------------

        /// <summary>
        /// Target voxel spacing [x, y, z] in mm, or null to keep each series' native grid.
        /// Images and dose resample linearly; masks resample nearest-neighbour.
        /// </summary>
        public double[] OutputSpacing { get; set; }

        // ---- Privacy ----------------------------------------------------------------------

        /// <summary>
        /// When true, output folders and manifest identifier columns carry salted hashes and an
        /// AnonymizationKey.json is written into <see cref="OutputRoot"/>.
        /// </summary>
        public bool Anonymize { get; set; }

        /// <summary>Salt for the deterministic hashes. The same salt reproduces the same folder names.</summary>
        public string Salt { get; set; } = "DicomToNifti";

        // ---- Metadata sidecar --------------------------------------------------------------

        /// <summary>DICOM keywords and "@" computed keys for the metadata.json image section.</summary>
        public List<string> ImageKeywords { get; set; } = new List<string>();

        /// <summary>DICOM keywords and "@" computed keys for the metadata.json structure section.</summary>
        public List<string> StructureKeywords { get; set; } = new List<string>();

        /// <summary>DICOM keywords and "@" computed keys for the metadata.json dose section.</summary>
        public List<string> DoseKeywords { get; set; } = new List<string>();

        /// <summary>True when any metadata section has at least one selection.</summary>
        public bool HasMetadataSelections =>
            (ImageKeywords != null && ImageKeywords.Count > 0) ||
            (StructureKeywords != null && StructureKeywords.Count > 0) ||
            (DoseKeywords != null && DoseKeywords.Count > 0);

        // ---- Run behaviour -----------------------------------------------------------------

        /// <summary>Manifest filename written into <see cref="OutputRoot"/>.</summary>
        public string ManifestFileName { get; set; } = "export_manifest.csv";

        /// <summary>
        /// Compute per-ROI volumes. Rasterization is the only way to get them — there is no
        /// analytic contour-area path — so turning this off makes a manifest run cheap at the
        /// cost of leaving every volume cell at the missing sentinel.
        /// </summary>
        public bool ComputeVolumes { get; set; } = true;

        /// <summary>
        /// Abort the whole run on the first series that fails. Off by default: a cohort export
        /// should get through the other 39 patients and report the one that broke.
        /// </summary>
        public bool FailFast { get; set; }

        /// <summary>
        /// Parses an "X,Y,Z" spacing argument in mm. Returns false with a human-readable
        /// <paramref name="error"/> rather than throwing, so callers can map it to an exit code.
        /// </summary>
        public static bool TryParseSpacing(string text, out double[] spacing, out string error)
        {
            spacing = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "spacing is empty; expected 'X,Y,Z' in mm";
                return false;
            }

            var parts = text.Split(',');
            if (parts.Length != 3)
            {
                error = $"expected three comma-separated values 'X,Y,Z'; got '{text}'";
                return false;
            }

            var parsed = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed[i]))
                {
                    error = $"spacing component '{parts[i].Trim()}' is not a number";
                    return false;
                }
                if (parsed[i] <= 0)
                {
                    error = $"spacing component '{parts[i].Trim()}' must be greater than zero";
                    return false;
                }
            }

            spacing = parsed;
            return true;
        }

        /// <summary>
        /// Splits a comma-separated keyword list, trimming whitespace and dropping empties.
        /// </summary>
        public static List<string> ParseKeywordList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return text.Split(',')
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();
        }
    }
}
