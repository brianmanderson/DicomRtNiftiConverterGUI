using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// The cohort analysis as flat tables (<c>--csv-out FOLDER</c>), one CSV per table, long
    /// format: a row per image series, per ROI of a linked structure set, per ROI name, and per
    /// flag. Their own schema, deliberately apart from the export manifest, whose first six
    /// columns are frozen so earlier manifests keep merging. Built here so every front-end writes
    /// the same bytes: every text field quoted, numbers invariant, "\n" line ends, a missing
    /// value empty, lists joined with ';'.
    /// </summary>
    public static class CohortAnalysisCsv
    {
        public const string SeriesFile = "analysis_series.csv";
        public const string RoisFile = "analysis_rois.csv";
        public const string RoiNamesFile = "analysis_roi_names.csv";
        public const string FlagsFile = "analysis_flags.csv";

        /// <summary>The contours tier's: a row per ROI of a linked structure set that was read.</summary>
        public const string RoiContoursFile = "analysis_roi_contours.csv";

        /// <summary>The contours tier's: a row per linked dose of an image series.</summary>
        public const string DosesFile = "analysis_doses.csv";

        /// <summary>
        /// The four tables, file name first, in that order; the contours tier adds
        /// <see cref="RoiContoursFile"/> and <see cref="DosesFile"/> after them, so the header
        /// tier's four are the same files at every tier.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> Build(CohortAnalysis analysis)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            var tables = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(SeriesFile, SeriesTable(analysis)),
                new KeyValuePair<string, string>(RoisFile, RoisTable(analysis)),
                new KeyValuePair<string, string>(RoiNamesFile, RoiNamesTable(analysis)),
                new KeyValuePair<string, string>(FlagsFile, FlagsTable(analysis)),
            };
            if (analysis.Tier != AnalysisTierNames.Header)
            {
                tables.Add(new KeyValuePair<string, string>(RoiContoursFile, RoiContoursTable(analysis)));
                tables.Add(new KeyValuePair<string, string>(DosesFile, DosesTable(analysis)));
            }
            return tables;
        }

        private static string SeriesTable(CohortAnalysis analysis)
        {
            var table = new Table(
                "patient_id", "study_uid", "series_uid", "modality", "series_description",
                "instance_count", "slice_count", "unpositioned_slices",
                "pixel_spacing_row_mm", "pixel_spacing_col_mm", "spacing_x_mm", "spacing_y_mm", "spacing_z_mm",
                "slice_spacing_uniform", "slice_spacing_values_mm", "spacing_error_factor",
                "z_min_mm", "z_max_mm", "extent_mm", "slice_thickness_mm", "rows", "columns", "fov_x_mm", "fov_y_mm",
                "plane", "axis_aligned", "normal_tilt_deg",
                "rescale_slope", "rescale_intercept", "rescale_varies", "number_of_frames", "modality_counts",
                "rtstructs", "default_rtstruct", "rtdoses", "flags");
            foreach (var s in analysis.Series)
            {
                var g = s.Geometry;
                var o = s.Orientation;
                table.Row(
                    Text(s.PatientId), Text(s.StudyUid), Text(s.SeriesUid), Text(s.Modality), Text(s.SeriesDescription),
                    Number(g.InstanceCount), Number(g.SliceCount), Number(g.UnpositionedSlices),
                    Number(At(g.PixelSpacing, 0)), Number(At(g.PixelSpacing, 1)),
                    Number(At(g.Spacing, 0)), Number(At(g.Spacing, 1)), Number(At(g.Spacing, 2)),
                    Bool(g.SliceSpacingUniform), Text(List(g.SliceSpacingValues?.Select(v => Number(v)))), Number(g.SpacingErrorFactor),
                    Number(g.ZMin), Number(g.ZMax), Number(g.ExtentMm), Number(g.SliceThickness),
                    Number(g.Rows), Number(g.Columns), Number(At(g.FieldOfViewMm, 0)), Number(At(g.FieldOfViewMm, 1)),
                    Text(o?.Plane), Bool(o?.AxisAligned), Number(o?.NormalTiltDegrees),
                    Number(s.Header.RescaleSlope), Number(s.Header.RescaleIntercept), Bool(s.Header.RescaleVaries),
                    Number(s.Header.NumberOfFrames),
                    Text(List(s.Header.ModalityCounts.Select(c => c.Key + "=" + Number(c.Value)))),
                    Number(s.Links.Rtstructs.Count), Text(s.Links.DefaultRtstruct), Number(s.Dose.Count),
                    Text(List(s.Flags)));
            }
            return table.ToString();
        }

        private static string RoisTable(CohortAnalysis analysis)
        {
            var table = new Table(
                "patient_id", "study_uid", "series_uid", "structure_set_uid", "default_structure_set",
                "roi_name", "canonical_name", "associated", "output_name", "kept_if_only_associated", "duplicate");
            foreach (var s in analysis.Series)
            {
                foreach (var roi in s.Rois)
                {
                    table.Row(
                        Text(s.PatientId), Text(s.StudyUid), Text(s.SeriesUid), Text(roi.StructureSetUid),
                        Bool(roi.StructureSetUid == s.Links.DefaultRtstruct),
                        Text(roi.Name), Text(roi.CanonicalName), Bool(roi.Associated), Text(roi.OutputName),
                        Bool(roi.KeptIfOnlyAssociated), Bool(roi.Duplicate));
                }
            }
            return table.ToString();
        }

        private static string RoiContoursTable(CohortAnalysis analysis)
        {
            var table = new Table(
                "patient_id", "study_uid", "series_uid", "structure_set_uid", "roi_name",
                "contours", "usable_contours", "parse_failed", "too_few_points", "degenerate_contours", "points",
                "geometric_types", "planes", "z_min_mm", "z_max_mm", "z_extent_mm",
                "bounds_min_x_mm", "bounds_min_y_mm", "bounds_min_z_mm", "bounds_max_x_mm", "bounds_max_y_mm", "bounds_max_z_mm",
                "grid_checked", "slices_with_contours", "off_plane_contours", "outside_fov_contours", "partly_outside_fov_contours",
                "empty", "degenerate");
            foreach (var s in analysis.Series)
            {
                foreach (var roi in s.Rois.Where(r => r.Contours != null))
                {
                    var c = roi.Contours;
                    table.Row(
                        Text(s.PatientId), Text(s.StudyUid), Text(s.SeriesUid), Text(roi.StructureSetUid), Text(roi.Name),
                        Number(c.Contours), Number(c.UsableContours), Number(c.ParseFailed), Number(c.TooFewPoints),
                        Number(c.DegenerateContours), Number(c.Points),
                        Text(List(c.GeometricTypes.Select(t => t.Key + "=" + Number(t.Value)))), Number(c.Planes),
                        Number(c.ZMin), Number(c.ZMax), Number(c.ZExtentMm),
                        Number(At(c.BoundsMinMm, 0)), Number(At(c.BoundsMinMm, 1)), Number(At(c.BoundsMinMm, 2)),
                        Number(At(c.BoundsMaxMm, 0)), Number(At(c.BoundsMaxMm, 1)), Number(At(c.BoundsMaxMm, 2)),
                        Bool(c.GridChecked), Number(c.SlicesWithContours), Number(c.OffPlaneContours),
                        Number(c.OutsideFovContours), Number(c.PartlyOutsideFovContours), Bool(c.Empty), Bool(c.Degenerate));
                }
            }
            return table.ToString();
        }

        private static string DosesTable(CohortAnalysis analysis)
        {
            var table = new Table(
                "patient_id", "study_uid", "series_uid", "dose_uid", "readable", "dose_units", "dose_type", "dose_summation_type",
                "rows", "columns", "frames", "voxel_x_mm", "voxel_y_mm", "voxel_z_mm", "max_dose",
                "orientation_matches_image", "same_grid_as_image", "rois_checked", "rois_not_covered");
            foreach (var s in analysis.Series)
            {
                foreach (var dose in s.Dose.Where(d => d.Audit != null))
                {
                    var a = dose.Audit;
                    var coverage = a.RoiCoverage ?? new List<RoiDoseCoverage>();
                    table.Row(
                        Text(s.PatientId), Text(s.StudyUid), Text(s.SeriesUid), Text(dose.SeriesUid), Bool(a.Error == null),
                        Text(a.DoseUnits), Text(a.DoseType), Text(a.DoseSummationType),
                        Number(a.Rows), Number(a.Columns), Number(a.Frames),
                        Number(At(a.VoxelSizeMm, 0)), Number(At(a.VoxelSizeMm, 1)), Number(At(a.VoxelSizeMm, 2)), Number(a.MaxDose),
                        Bool(a.OrientationMatchesImage), Bool(a.SameGridAsImage),
                        Number(coverage.Count), Text(List(coverage.Where(r => !r.Covered).Select(r => r.Roi))));
                }
            }
            return table.ToString();
        }

        private static string RoiNamesTable(CohortAnalysis analysis)
        {
            var table = new Table(
                "roi_name", "series", "patients", "structure_sets", "normalized", "canonical_name", "associated",
                "dropped_if_only_associated");
            var dropped = analysis.RoiNaming.DroppedIfOnlyAssociated.ToDictionary(d => d.Name, d => d.Series, StringComparer.OrdinalIgnoreCase);
            foreach (var n in analysis.RoiNaming.Names)
            {
                dropped.TryGetValue(n.Name, out int droppedSeries);
                table.Row(
                    Text(n.Name), Number(n.Series), Number(n.Patients), Number(n.StructureSets), Text(n.Normalized),
                    Text(n.CanonicalName), Bool(n.Associated), Number(droppedSeries));
            }
            return table.ToString();
        }

        private static string FlagsTable(CohortAnalysis analysis)
        {
            var table = new Table("code", "severity", "patient_id", "study_uid", "series_uid", "object_uid", "roi", "message");
            foreach (var f in analysis.Flags)
            {
                table.Row(
                    Text(f.Code), Text(f.Severity), Text(f.PatientId), Text(f.StudyUid), Text(f.SeriesUid),
                    Text(f.ObjectUid), Text(f.Roi), Text(f.Message));
            }
            return table.ToString();
        }

        private static double? At(double[] values, int index) =>
            values != null && values.Length > index ? values[index] : (double?)null;

        private static string List(IEnumerable<string> values) => values == null ? null : string.Join(";", values);

        /// <summary>Quoted, with embedded quotes doubled; null is an empty field.</summary>
        private static string Text(string value) =>
            value == null ? "" : "\"" + value.Replace("\"", "\"\"") + "\"";

        private static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";

        private static string Number(int? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Bool(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "";

        private sealed class Table
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly int _columns;

            public Table(params string[] header)
            {
                _columns = header.Length;
                _text.Append(string.Join(",", header)).Append('\n');
            }

            public void Row(params string[] cells)
            {
                if (cells.Length != _columns)
                    throw new InvalidOperationException($"A row of {cells.Length} cells in a table of {_columns} columns.");
                _text.Append(string.Join(",", cells)).Append('\n');
            }

            public override string ToString() => _text.ToString();
        }
    }
}
