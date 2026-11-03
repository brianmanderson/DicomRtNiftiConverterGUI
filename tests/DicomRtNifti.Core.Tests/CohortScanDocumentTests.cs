using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The two additions to the scan document: unpositioned_slices per image series (F-12,
    /// toolkit issue 45), and --anonymize, which until now the inventory did not have, so it always
    /// printed real PatientIDs, UIDs and the input path.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortScanDocumentTests : IClassFixture<GoldenCohortFixture>, IDisposable
    {
        private readonly GoldenCohortFixture _cohort;
        private readonly string _dir = DicomTestData.NewTempDir();

        public CohortScanDocumentTests(GoldenCohortFixture cohort)
        {
            _cohort = cohort;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// A series reduced before its spacing was judged says so: the slice that had no
        /// ImagePositionPatient is named, and a clean series carries an empty list.
        /// </summary>
        [Fact]
        public void UnpositionedSlices_NameTheSkippedSlice_AndAreEmptyOnACleanSeries()
        {
            var scan = Scan("--cohort-scan", "--input", _cohort.Input);

            var series = Series(scan);
            Assert.Equal(new[] { "ct_006.dcm (InstanceNumber 7): ImagePositionPatient missing" },
                series["1.2.3.200.2"]["unpositioned_slices"].Select(v => (string)v));
            Assert.All(series.Where(s => s.Key != "1.2.3.200.2"), s => Assert.Empty(s.Value["unpositioned_slices"]));
        }

        /// <summary>
        /// No source identifier, path, date or file name survives; the hashes are the ones the
        /// anonymized conversion writes for the same salt, so the inventory and the export can be
        /// joined on them.
        /// </summary>
        [Fact]
        public void Anonymized_CarriesTheExportsHashes_AndNoSourceIdentifier()
        {
            var run = CliRun.Execute("--cohort-scan", "--input", _cohort.Input, "--anonymize", "--salt", GoldenCohort.Salt);
            Assert.True(run.ExitCode == 0, run.ToString());
            var scan = JObject.Parse(run.Stdout);

            foreach (var leak in new[] { "PAT_", "1.2.3.", ".dcm", _cohort.Root.Replace("\\", "\\\\") })
                Assert.DoesNotContain(leak, run.Stdout);
            Assert.Equal(JTokenType.Null, scan["input_root"].Type);
            Assert.All(Series(scan).Values, s =>
            {
                Assert.Equal(JTokenType.Null, s["series_date"].Type);
                Assert.Equal(JTokenType.Null, s["frame_of_reference_uid"].Type);
            });
            Assert.Contains("(InstanceNumber 7): ImagePositionPatient missing",
                Series(scan).Values.SelectMany(s => s["unpositioned_slices"]).Select(v => (string)v));

            var convert = JObject.Parse(CliRun.Execute("--cohort-convert", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "out"), "--anonymize", "--salt", GoldenCohort.Salt).Stdout);
            var exported = convert["series"].Concat(convert["errors"])
                .Select(s => (string)s["patient_id"] + "/" + (string)s["series_uid"])
                .OrderBy(k => k, StringComparer.Ordinal);
            var inventoried = scan["patients"]
                .SelectMany(p => p["studies"].SelectMany(st => st["series"])
                    .Select(s => (string)p["patient_id"] + "/" + (string)s["series_instance_uid"]))
                .OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(exported, inventoried);
        }

        /// <summary>
        /// --key-path is read, so a manual override shows; nothing is written, there or anywhere
        /// else, because a scan has no export to key.
        /// </summary>
        [Fact]
        public void Anonymized_ReadsTheKeyAtKeyPath_AndNeverWritesOne()
        {
            string keyFile = Path.Combine(_dir, "keys", AnonymizationKeyLocation.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile));
            AnonymizationService.SaveKeyFile(keyFile, new AnonymizationKeyFile
            {
                Salt = GoldenCohort.Salt,
                Patients = new Dictionary<string, string> { { "PAT_D", "P_OVERRIDE" } },
                Studies = new Dictionary<string, string>(),
                Series = new Dictionary<string, string>(),
            });
            byte[] before = File.ReadAllBytes(keyFile);
            string missing = Path.Combine(_dir, "nokey", AnonymizationKeyLocation.FileName);

            var withKey = Scan("--cohort-scan", "--input", _cohort.Input, "--anonymize",
                "--salt", GoldenCohort.Salt, "--key-path", Path.Combine(_dir, "keys"));
            var withoutKey = Scan("--cohort-scan", "--input", _cohort.Input, "--anonymize",
                "--salt", GoldenCohort.Salt, "--key-path", missing);

            Assert.Contains(withKey["patients"], p => (string)p["patient_id"] == "P_OVERRIDE");
            Assert.DoesNotContain(withoutKey["patients"], p => (string)p["patient_id"] == "P_OVERRIDE");
            Assert.Equal(before, File.ReadAllBytes(keyFile));
            Assert.Single(Directory.GetFiles(Path.Combine(_dir, "keys")));
            Assert.False(Directory.Exists(Path.Combine(_dir, "nokey")));
        }

        [Fact]
        public void Anonymized_AKeyUnderAnotherSalt_IsRefused()
        {
            string keyFile = Path.Combine(_dir, AnonymizationKeyLocation.FileName);
            AnonymizationService.SaveKeyFile(keyFile, new AnonymizationKeyFile
            {
                Salt = "another salt",
                Patients = new Dictionary<string, string> { { "PAT_A", "P1" } },
                Studies = new Dictionary<string, string>(),
                Series = new Dictionary<string, string>(),
            });

            var run = CliRun.Execute("--cohort-scan", "--input", _cohort.Input, "--anonymize",
                "--salt", GoldenCohort.Salt, "--key-path", keyFile);

            Assert.True(run.ExitCode == 1, run.ToString());
            Assert.Equal("", run.Stdout);
        }

        private static JObject Scan(params string[] args)
        {
            var run = CliRun.Execute(args);
            Assert.True(run.ExitCode == 0, run.ToString());
            return JObject.Parse(run.Stdout);
        }

        private static Dictionary<string, JToken> Series(JObject scan) =>
            scan["patients"]
                .SelectMany(p => p["studies"].SelectMany(st => st["series"]))
                .ToDictionary(s => (string)s["series_instance_uid"]);
    }
}
