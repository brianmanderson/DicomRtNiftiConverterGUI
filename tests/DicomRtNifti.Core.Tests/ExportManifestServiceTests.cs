using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The manifest CSV is the cohort's growable index, so the merge semantics matter as much as
    /// the format: a second export must extend the file, not regenerate it. These characterize
    /// the behaviour that was previously buried (and untested) in the Avalonia view-model.
    /// </summary>
    public class ExportManifestServiceTests
    {
        private static ManifestRow Row(
            string pid, string study, string series,
            double sx = 1, double sy = 1, double sz = 3,
            Dictionary<string, double> volumes = null)
        {
            var row = new ManifestRow
            {
                PatientID = pid,
                StudyUID = study,
                SeriesUID = series,
                SpacingX = sx,
                SpacingY = sy,
                SpacingZ = sz,
            };
            if (volumes != null)
                foreach (var kv in volumes)
                    row.RoiVolumes[kv.Key] = kv.Value;
            return row;
        }

        private static string TempCsv() =>
            Path.Combine(DicomTestData.NewTempDir(), ExportManifestService.DefaultFileName);

        private static void Cleanup(string csvPath) =>
            Directory.Delete(Path.GetDirectoryName(csvPath), recursive: true);

        [Fact]
        public void RoundTrips_IdentifiersSpacingAndVolumes()
        {
            string csv = TempCsv();
            try
            {
                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1", 0.9766, 0.9766, 3.0,
                        new Dictionary<string, double> { ["Pancreas"] = 23.4 }) },
                    new[] { "Pancreas" });

                Assert.True(ExportManifestService.TryRead(csv, out var cols, out var rows));

                Assert.Equal(new[] { "Pancreas" }, cols);
                var row = Assert.Single(rows);
                Assert.Equal("P1", row.PatientID);
                Assert.Equal("ST1", row.StudyUID);
                Assert.Equal("SE1", row.SeriesUID);
                Assert.Equal(0.9766, row.SpacingX, 4);
                Assert.Equal(3.0, row.SpacingZ, 4);
                Assert.Equal(23.4, row.RoiVolumes["Pancreas"], 4);
            }
            finally { Cleanup(csv); }
        }

        [Fact]
        public void SecondWrite_UpsertsMatchingKeyAndAppendsNewOne()
        {
            string csv = TempCsv();
            try
            {
                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1", 1, 1, 3,
                        new Dictionary<string, double> { ["Pancreas"] = 10 }) },
                    new[] { "Pancreas" });

                // Same key with new spacing and volume, plus a brand-new series.
                ExportManifestService.Write(
                    csv,
                    new[]
                    {
                        Row("P1", "ST1", "SE1", 2, 2, 5,
                            new Dictionary<string, double> { ["Pancreas"] = 11 }),
                        Row("P2", "ST2", "SE2", 1, 1, 3,
                            new Dictionary<string, double> { ["Pancreas"] = 20 }),
                    },
                    new[] { "Pancreas" });

                ExportManifestService.TryRead(csv, out _, out var rows);

                Assert.Equal(2, rows.Count);
                var first = rows.First(r => r.SeriesUID == "SE1");
                Assert.Equal(5, first.SpacingZ, 4);
                Assert.Equal(11, first.RoiVolumes["Pancreas"], 4);
                // The pre-existing row keeps its position; the new one is appended after it.
                Assert.Equal("SE1", rows[0].SeriesUID);
                Assert.Equal("SE2", rows[1].SeriesUID);
            }
            finally { Cleanup(csv); }
        }

        [Fact]
        public void NewRoiColumn_IsAppended_ExistingColumnOrderPreserved()
        {
            string csv = TempCsv();
            try
            {
                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1", volumes: new Dictionary<string, double>
                        { ["Pancreas"] = 10, ["Duodenum"] = 5 }) },
                    new[] { "Pancreas", "Duodenum" });

                ExportManifestService.Write(
                    csv,
                    new[] { Row("P2", "ST2", "SE2", volumes: new Dictionary<string, double>
                        { ["Stomach"] = 7 }) },
                    new[] { "Stomach" });

                ExportManifestService.TryRead(csv, out var cols, out var rows);

                Assert.Equal(new[] { "Pancreas", "Duodenum", "Stomach" }, cols);
                // The row written before "Stomach" existed reports the missing sentinel for it.
                var first = rows.First(r => r.SeriesUID == "SE1");
                Assert.Equal(ExportManifestService.MissingValue, first.RoiVolumes["Stomach"], 4);
            }
            finally { Cleanup(csv); }
        }

        [Fact]
        public void MissingVolume_IsWrittenAsSentinelNotBlank()
        {
            string csv = TempCsv();
            try
            {
                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1") },
                    new[] { "Pancreas" });

                string dataLine = File.ReadAllLines(csv)[1];
                Assert.EndsWith(",-1", dataLine);
            }
            finally { Cleanup(csv); }
        }

        [Fact]
        public void RoiNameWithCommaAndQuote_SurvivesRoundTrip()
        {
            string csv = TempCsv();
            try
            {
                // An ROI named with a comma would split into two columns unquoted; a quote
                // would terminate the field early unless doubled.
                const string nasty = "GTV, \"boost\"";
                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1",
                        volumes: new Dictionary<string, double> { [nasty] = 42 }) },
                    new[] { nasty });

                ExportManifestService.TryRead(csv, out var cols, out var rows);

                Assert.Equal(nasty, Assert.Single(cols));
                Assert.Equal(42, rows[0].RoiVolumes[nasty], 4);
            }
            finally { Cleanup(csv); }
        }

        [Fact]
        public void NumbersAreWrittenInvariant_EvenOnCommaDecimalLocale()
        {
            string csv = TempCsv();
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                // de-DE writes 3,5 for 3.5. Ambient formatting would split that across two CSV
                // columns and silently corrupt every row.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                ExportManifestService.Write(
                    csv,
                    new[] { Row("P1", "ST1", "SE1", 0.5, 0.5, 3.5,
                        volumes: new Dictionary<string, double> { ["Pancreas"] = 23.4 }) },
                    new[] { "Pancreas" });

                string dataLine = File.ReadAllLines(csv)[1];
                Assert.Contains("3.5", dataLine);
                Assert.Contains("23.4", dataLine);

                // Seven fields: three quoted identifiers, three spacings, one ROI volume.
                Assert.Equal(7, ExportManifestService.ParseCsvLine(dataLine).Count);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                Cleanup(csv);
            }
        }

        [Fact]
        public void CurrentCultureCells_AreStillReadable_ForManifestsFromOlderBuilds()
        {
            string csv = TempCsv();
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(3.5, ExportManifestService.ParseDouble("3,5"), 4);
                Assert.Equal(3.5, ExportManifestService.ParseDouble("3.5"), 4);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                Cleanup(csv);
            }
        }

        [Fact]
        public void UnreadableOrMissingFile_YieldsFreshWrite()
        {
            string csv = TempCsv();
            try
            {
                Assert.False(ExportManifestService.TryRead(csv, out var cols, out var rows));
                Assert.Empty(cols);
                Assert.Empty(rows);

                File.WriteAllText(csv, "");
                Assert.False(ExportManifestService.TryRead(csv, out _, out _));

                ExportManifestService.Write(csv, new[] { Row("P1", "ST1", "SE1") }, new[] { "Pancreas" });
                Assert.True(ExportManifestService.TryRead(csv, out _, out var written));
                Assert.Single(written);
            }
            finally { Cleanup(csv); }
        }
    }
}
