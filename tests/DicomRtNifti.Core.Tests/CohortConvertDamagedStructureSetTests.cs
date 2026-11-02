using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A structure set damaged in its contour data is listed by the scan (which stops reading at
    /// the contours) and fails when a conversion reads it in full. The cohort export reports that
    /// one series and converts the others: exit 0, the failure in the JSON's errors, the manifest
    /// holding the series that converted.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortConvertDamagedStructureSetTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void Cohort_convert_reports_the_damaged_series_and_converts_the_rest()
        {
            string input = Path.Combine(_dir, "dicom");
            string output = Path.Combine(_dir, "out");
            DicomTestData.WritePatientWithStructureSet(input, "PAT_A", damagedContours: false);
            DicomTestData.WritePatientWithStructureSet(input, "PAT_B", damagedContours: true);
            DicomTestData.WritePatientWithStructureSet(input, "PAT_C", damagedContours: false);

            var run = CliRun.Execute("--cohort-convert", "--input", input, "--output", output, "--no-doses");

            Assert.True(run.ExitCode == 0, run.ToString());
            var json = JObject.Parse(run.Stdout);
            Assert.Equal(2, (int)json["succeeded"]);
            Assert.Equal(1, (int)json["failed"]);
            Assert.Equal("PAT_B", (string)Assert.Single((JArray)json["errors"])["patient_id"]);
            Assert.Equal(new[] { "PAT_A", "PAT_C" },
                ((JArray)json["series"]).Select(s => (string)s["patient_id"]).OrderBy(p => p).ToArray());
            Assert.All((JArray)json["series"], s => Assert.Equal("Target", (string)Assert.Single((JArray)s["masks"])["name"]));

            var manifest = File.ReadAllLines(Path.Combine(output, (string)json["manifest_csv"] ?? "export_manifest.csv"));
            Assert.Contains(manifest, l => l.StartsWith("\"PAT_A\",", StringComparison.Ordinal));
            Assert.Contains(manifest, l => l.StartsWith("\"PAT_C\",", StringComparison.Ordinal));
            Assert.DoesNotContain(manifest, l => l.StartsWith("\"PAT_B\",", StringComparison.Ordinal));
        }
    }
}
