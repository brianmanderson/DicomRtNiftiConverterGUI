using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Reads and writes the cohort export manifest CSV — one row per exported series, carrying
    /// the three identifier columns, the image spacing, and one column per ROI holding that
    /// ROI's mask volume in cc.
    ///
    /// Writes are incremental: an existing manifest is merged rather than regenerated, so a
    /// cohort can grow across runs. Rows are keyed by (PatientID, StudyUID, SeriesUID); a
    /// matching incoming row updates spacing and overlays ROI volumes, a new key is appended,
    /// and ROI columns are the union of what is already in the file and what this export
    /// produced, with existing columns kept in place.
    ///
    /// The first six columns are a fixed schema and <see cref="TryRead"/> treats everything
    /// after them as ROI columns — so additional per-series fields must go somewhere else
    /// (the cohort JSON documents), never into this header, or previously written manifests
    /// stop merging.
    /// </summary>
    public static class ExportManifestService
    {
        /// <summary>Default manifest filename written alongside a cohort export.</summary>
        public const string DefaultFileName = "export_manifest.csv";

        /// <summary>The fixed leading columns; everything after these is an ROI volume column.</summary>
        private const string FixedHeader = "PatientID,StudyUID,SeriesUID,SpacingX,SpacingY,SpacingZ";

        private const int FixedColumnCount = 6;

        /// <summary>Value written for a row that has no volume for a given ROI column.</summary>
        public const double MissingValue = -1;

        /// <summary>
        /// Writes <paramref name="rows"/> to <paramref name="csvPath"/>, merging into an existing
        /// manifest when one is present. Returns the path written.
        /// </summary>
        /// <param name="roiColumnNames">
        /// ROI columns introduced by this export. Unioned into any columns already in the file,
        /// preserving the existing column order so the file grows predictably.
        /// </param>
        public static string Write(
            string csvPath,
            IEnumerable<ManifestRow> rows,
            IReadOnlyList<string> roiColumnNames = null)
        {
            var mergedByKey = new Dictionary<(string, string, string), ManifestRow>();
            var orderedKeys = new List<(string, string, string)>();
            var roiColumns = new List<string>();
            var roiColumnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Seed from the existing manifest (if any) so we extend rather than regenerate.
            if (TryRead(csvPath, out var existingRoiColumns, out var existingRows))
            {
                foreach (var name in existingRoiColumns)
                    if (roiColumnSet.Add(name))
                        roiColumns.Add(name);
                foreach (var row in existingRows)
                {
                    var key = Key(row);
                    if (!mergedByKey.ContainsKey(key))
                        orderedKeys.Add(key);
                    mergedByKey[key] = row;
                }
            }

            // Overlay the incoming rows: update matching keys in place, append new ones.
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    var key = Key(row);
                    if (mergedByKey.TryGetValue(key, out var existing))
                    {
                        existing.SpacingX = row.SpacingX;
                        existing.SpacingY = row.SpacingY;
                        existing.SpacingZ = row.SpacingZ;
                        if (row.RoiVolumes != null)
                            foreach (var rv in row.RoiVolumes)
                                existing.RoiVolumes[rv.Key] = rv.Value;
                    }
                    else
                    {
                        mergedByKey[key] = row;
                        orderedKeys.Add(key);
                    }
                }
            }

            // Union in any ROI columns introduced by this export, preserving existing column order.
            if (roiColumnNames != null)
                foreach (var roiName in roiColumnNames)
                    if (roiColumnSet.Add(roiName))
                        roiColumns.Add(roiName);

            string directory = Path.GetDirectoryName(Path.GetFullPath(csvPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(csvPath))
            {
                var header = new StringBuilder(FixedHeader);
                foreach (var roiName in roiColumns)
                    header.Append(",\"").Append(roiName.Replace("\"", "\"\"")).Append('"');
                writer.WriteLine(header.ToString());

                foreach (var key in orderedKeys)
                {
                    var row = mergedByKey[key];
                    var line = new StringBuilder();
                    line.Append(Quote(row.PatientID)).Append(',')
                        .Append(Quote(row.StudyUID)).Append(',')
                        .Append(Quote(row.SeriesUID)).Append(',')
                        .Append(Num(row.SpacingX)).Append(',')
                        .Append(Num(row.SpacingY)).Append(',')
                        .Append(Num(row.SpacingZ));

                    foreach (var roiName in roiColumns)
                    {
                        double volume = (row.RoiVolumes != null &&
                                         row.RoiVolumes.TryGetValue(roiName, out double v))
                            ? v
                            : MissingValue;
                        line.Append(',').Append(Num(volume));
                    }
                    writer.WriteLine(line.ToString());
                }
            }

            return csvPath;
        }

        /// <summary>
        /// Reads an existing manifest into its ROI column list and rows, both in file order.
        /// Returns false when the file is missing, empty, or unreadable, in which case the out
        /// parameters are empty and the caller should write a fresh manifest.
        /// </summary>
        public static bool TryRead(string csvPath, out List<string> roiColumns, out List<ManifestRow> rows)
        {
            roiColumns = new List<string>();
            rows = new List<ManifestRow>();
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                return false;

            try
            {
                var lines = File.ReadAllLines(csvPath);
                if (lines.Length == 0)
                    return false;

                var header = ParseCsvLine(lines[0]);
                for (int i = FixedColumnCount; i < header.Count; i++)
                    roiColumns.Add(header[i]);

                for (int r = 1; r < lines.Length; r++)
                {
                    if (string.IsNullOrWhiteSpace(lines[r]))
                        continue;
                    var fields = ParseCsvLine(lines[r]);
                    if (fields.Count < FixedColumnCount)
                        continue;

                    var row = new ManifestRow
                    {
                        PatientID = fields[0],
                        StudyUID = fields[1],
                        SeriesUID = fields[2],
                        SpacingX = ParseDouble(fields[3]),
                        SpacingY = ParseDouble(fields[4]),
                        SpacingZ = ParseDouble(fields[5]),
                    };
                    for (int i = FixedColumnCount;
                         i < fields.Count && (i - FixedColumnCount) < roiColumns.Count;
                         i++)
                    {
                        row.RoiVolumes[roiColumns[i - FixedColumnCount]] = ParseDouble(fields[i]);
                    }

                    rows.Add(row);
                }
                return true;
            }
            catch
            {
                roiColumns = new List<string>();
                rows = new List<ManifestRow>();
                return false;
            }
        }

        /// <summary>Composite row key for merging: the three identifier columns as a tuple.</summary>
        public static (string, string, string) Key(ManifestRow row)
            => (row.PatientID ?? "", row.StudyUID ?? "", row.SeriesUID ?? "");

        /// <summary>Parses a single CSV line into fields, honoring double-quoted fields and "" escapes.</summary>
        public static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>
        /// Parses a manifest numeric cell. Invariant is tried first because that is what
        /// <see cref="Write"/> emits; the current-culture fallback recovers cells written by
        /// older builds, which formatted numbers with the ambient locale.
        ///
        /// The style is <see cref="NumberStyles.Float"/> rather than <see cref="NumberStyles.Any"/>
        /// specifically so that fallback can fire. Any permits thousands separators, under which
        /// invariant parsing reads a de-DE cell of "3,5" as 3-thousand-5 — succeeding with the
        /// wrong value instead of failing through to the current-culture attempt. Manifest cells
        /// are plain decimals and never carry group separators, so Float loses nothing.
        /// </summary>
        public static double ParseDouble(string s)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return v;
            return MissingValue;
        }

        /// <summary>
        /// Formats a number for a CSV cell. Invariant culture is mandatory: on a locale that uses
        /// a comma as the decimal separator, ambient formatting would emit "23,4" and split one
        /// value across two columns.
        /// </summary>
        private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Quote(string value)
            => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
