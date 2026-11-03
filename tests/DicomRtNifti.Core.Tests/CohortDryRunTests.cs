using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// --dry-run on --cohort-convert and --cohort-manifest (D-42): the plan a run would carry
    /// out, printed as dicomrtnifti.cohort-plan/1, with nothing written. Two promises are pinned
    /// here: the plan is what the run then does (same folders, same identifiers, same ROI names,
    /// same warnings), and a dry run leaves no trace — no output folder, no key.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortDryRunTests : IClassFixture<GoldenCohortFixture>, IDisposable
    {
        private readonly GoldenCohortFixture _cohort;
        private readonly string _dir = DicomTestData.NewTempDir();

        public CohortDryRunTests(GoldenCohortFixture cohort)
        {
            _cohort = cohort;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// Every planned series is either converted, into the folder and under the identifiers the
        /// plan gave, with masks named as the plan's ROI list, or reported as an error under those
        /// identifiers. Anonymized too: the hashes the dry run computes without saving a key are
        /// the ones the run writes.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ThePlan_IsWhatTheConversionThenDoes(bool anonymize)
        {
            var extra = anonymize ? new[] { "--anonymize", "--salt", GoldenCohort.Salt } : new string[0];
            var plan = Json(Run(new[] { "--cohort-convert", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "planned"), "--dry-run" }.Concat(extra).ToArray()));
            var convert = Json(Run(new[] { "--cohort-convert", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "converted") }.Concat(extra).ToArray()));

            var planned = ((JArray)plan["series"]).ToList();
            Assert.Equal((int)convert["succeeded"] + (int)convert["failed"], (int)plan["series_count"]);
            Assert.Equal(planned.Count, (int)plan["series_count"]);

            var converted = ((JArray)convert["series"]).ToDictionary(s => (string)s["series_uid"]);
            var failed = ((JArray)convert["errors"]).Select(e => (string)e["series_uid"]).ToList();
            foreach (var p in planned)
            {
                string uid = (string)p["series_uid"];
                if (failed.Contains(uid))
                    continue;
                var c = converted[uid];
                Assert.Equal((string)p["patient_id"], (string)c["patient_id"]);
                Assert.Equal((string)p["study_uid"], (string)c["study_uid"]);
                Assert.Equal((string)p["output_dir"], (string)c["output_dir"]);
                Assert.Equal(p["rois"].Select(r => (string)r), c["masks"].Select(m => (string)m["name"]));
                Assert.Equal(p["doses"].Count(), c["doses"].Count());
                var structureSet = p["structure_set"];
                Assert.Equal(structureSet.Type == JTokenType.Null ? null : (string)structureSet["match_rule"],
                    (string)c["struct_link_rule"]);
            }
            Assert.Equal(converted.Count + failed.Count, planned.Count);
            Assert.Equal(plan["skipped"].ToString(), convert["skipped"].ToString());
        }

        /// <summary>
        /// The per-series warnings are the lines the run logs at the start of each series, word
        /// for word, matched by series position.
        /// </summary>
        [Fact]
        public void ThePlansWarnings_AreTheLinesTheRunLogs()
        {
            var plan = Json(Run("--cohort-manifest", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "planned"), "--no-volumes", "--dry-run"));
            var run = CliRun.Execute("--cohort-manifest", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "surveyed"), "--no-volumes");
            Assert.True(run.ExitCode == 0, run.ToString());

            // "  [i/n] label" opens a series in the log; its warnings follow, indented by the
            // runner's two spaces and the service's two.
            var logged = new Dictionary<int, List<string>>();
            int current = 0;
            foreach (var line in run.Stderr.Replace("\r\n", "\n").Split('\n'))
            {
                var header = Regex.Match(line, @"^  \[(\d+)/\d+\] ");
                if (header.Success)
                {
                    current = int.Parse(header.Groups[1].Value);
                    logged[current] = new List<string>();
                }
                else if (current > 0 && line.StartsWith("    WARNING: series ", StringComparison.Ordinal))
                {
                    logged[current].Add(line.Substring(4));
                }
            }

            var planned = ((JArray)plan["series"]).ToList();
            Assert.Equal(planned.Count, logged.Count);
            for (int i = 0; i < planned.Count; i++)
                Assert.Equal(logged[i + 1], planned[i]["warnings"].Select(w => (string)w));
            Assert.Contains(planned, p => p["warnings"].Count() == 2);
        }

        [Fact]
        public void Anonymized_ReadsAnExistingKey_AndLeavesItUntouched()
        {
            string output = Path.Combine(_dir, "out");
            string keyFile = Path.Combine(output + ".key", AnonymizationKeyLocation.FileName);
            WriteKey(keyFile, "PAT_A", "P_OVERRIDE");
            byte[] before = File.ReadAllBytes(keyFile);
            var stamp = File.GetLastWriteTimeUtc(keyFile);

            var plan = Json(Run("--cohort-convert", "--input", _cohort.Input, "--output", output,
                "--anonymize", "--salt", GoldenCohort.Salt, "--dry-run"));

            Assert.Equal(2, plan["series"].Count(s => (string)s["patient_id"] == "P_OVERRIDE"));
            Assert.Equal(Path.GetFullPath(keyFile), (string)plan["anonymization_key"]);
            Assert.Equal(before, File.ReadAllBytes(keyFile));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(keyFile));
            Assert.Single(Directory.GetFiles(output + ".key"));
            Assert.False(Directory.Exists(output));
        }

        /// <summary>
        /// A key an older export left inside the output root is read where it is. A real run
        /// would merge it into a new key beside the export; a dry run creates neither.
        /// </summary>
        [Fact]
        public void Anonymized_ReadsALegacyKeyInsideTheOutput_WithoutMergingIt()
        {
            string output = Path.Combine(_dir, "out");
            string legacy = Path.Combine(output, AnonymizationKeyLocation.FileName);
            WriteKey(legacy, "PAT_B", "P_LEGACY");
            byte[] before = File.ReadAllBytes(legacy);

            var run = CliRun.Execute("--cohort-manifest", "--input", _cohort.Input, "--output", output,
                "--anonymize", "--salt", GoldenCohort.Salt, "--dry-run");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("only reads them", run.Stderr);
            Assert.Single(Json(run.Stdout)["series"], s => (string)s["patient_id"] == "P_LEGACY");
            Assert.Equal(before, File.ReadAllBytes(legacy));
            Assert.Single(Directory.GetFileSystemEntries(output));
            Assert.False(Directory.Exists(output + ".key"));
        }

        [Fact]
        public void Anonymized_AKeyInsideTheOutput_IsRefusedAsTheRunWouldRefuseIt()
        {
            string output = Path.Combine(_dir, "out");

            var run = CliRun.Execute("--cohort-convert", "--input", _cohort.Input, "--output", output,
                "--anonymize", "--key-path", Path.Combine(output, "key.json"), "--dry-run");

            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Contains("inside the export folder", run.Stderr);
            Assert.False(Directory.Exists(output));
        }

        [Fact]
        public void JsonOut_IsWritten_UnlessItLiesInsideTheOutput()
        {
            string output = Path.Combine(_dir, "out");
            string jsonOut = Path.Combine(_dir, "plans", "plan.json");

            var run = CliRun.Execute("--cohort-convert", "--input", _cohort.Input, "--output", output,
                "--dry-run", "--json-out", jsonOut);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(run.Stdout.TrimEnd('\r', '\n'), File.ReadAllText(jsonOut));

            var inside = CliRun.Execute("--cohort-convert", "--input", _cohort.Input, "--output", output,
                "--dry-run", "--json-out", Path.Combine(output, "plan.json"));
            Assert.True(inside.ExitCode == 2, inside.ToString());
            Assert.Equal("", inside.Stdout);
            Assert.False(Directory.Exists(output));
        }

        /// <summary>
        /// The association typo a run only reports after converting everything (exit 1, no mask)
        /// is in the plan, in the run's words, before anything runs.
        /// </summary>
        [Fact]
        public void OnlyAssociatedRois_ThatMatchNothing_IsWarnedInThePlan()
        {
            string associations = Path.Combine(_dir, "associations.json");
            new SettingsService().ExportAssociations(new List<RoiAssociation>
            {
                new RoiAssociation { CanonicalName = "Liver", Aliases = { "liver" } },
            }, associations);

            var plan = Json(Run("--cohort-convert", "--input", _cohort.Input, "--output", Path.Combine(_dir, "out"),
                "--associations", associations, "--only-associated-rois", "--dry-run"));
            var bare = Json(Run("--cohort-manifest", "--input", _cohort.Input, "--output", Path.Combine(_dir, "out"),
                "--only-associated-rois", "--dry-run"));

            Assert.All(plan["series"], s => Assert.Empty(s["rois"]));
            Assert.StartsWith("WARNING: --only-associated-rois matched no ROI in any of the 4 exported structure set(s)",
                (string)Assert.Single(plan["warnings"]));
            Assert.Equal(CohortExportService.OnlyAssociatedWithoutAssociationsWarning, (string)Assert.Single(bare["warnings"]));
            Assert.Equal(new[] { "Ring", "Target" }, bare["series"][0]["rois"].Select(r => (string)r));
        }

        /// <summary>Runs the CLI, requires exit 0 and that the output folder was not created, and returns stdout.</summary>
        private static string Run(params string[] args)
        {
            var run = CliRun.Execute(args);
            Assert.True(run.ExitCode == 0, run.ToString());
            if (args.Contains("--dry-run"))
                Assert.False(Directory.Exists(args[Array.IndexOf(args, "--output") + 1]), run.ToString());
            return run.Stdout;
        }

        private static JObject Json(string stdout) => JObject.Parse(stdout);

        private static void WriteKey(string path, string patientId, string hash)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AnonymizationService.SaveKeyFile(path, new AnonymizationKeyFile
            {
                Salt = GoldenCohort.Salt,
                Patients = new Dictionary<string, string> { { patientId, hash } },
                Studies = new Dictionary<string, string>(),
                Series = new Dictionary<string, string>(),
            });
        }
    }
}
