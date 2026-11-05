using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// --series, --struct-series and --rois from the command line through to the documents: the
    /// plan a dry run prints carries them (so a front-end can show what its selection will do),
    /// and --rois that matches nothing fails the run as --only-associated-rois does. Over the
    /// golden cohort (see <see cref="GoldenCohort"/>): PAT_A's two CTs 1.2.3.100.2 (with the
    /// structure set 1.2.3.100.3, ROIs Target and Ring) and 1.2.3.100.5; PAT_B, PAT_D, PAT_E one
    /// CT each.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortSelectionCliTests : IClassFixture<GoldenCohortFixture>, IDisposable
    {
        private readonly GoldenCohortFixture _cohort;
        private readonly string _dir = DicomTestData.NewTempDir();

        public CohortSelectionCliTests(GoldenCohortFixture cohort)
        {
            _cohort = cohort;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void DryRun_Series_PlansTheListedSeries_AndSaysWhyEveryOtherIsLeftOut()
        {
            var plan = DryRun("--series", "1.2.3.100.5,1.2.3.400.2");

            Assert.Equal(new[] { "1.2.3.100.5", "1.2.3.400.2" }, plan["series"].Select(s => (string)s["series_uid"]));
            var skipped = plan["skipped"].ToDictionary(s => (string)s["series_instance_uid"], s => (string)s["reason"]);
            Assert.Equal(new[] { "1.2.3.100.2", "1.2.3.200.2", "1.2.3.500.2" }, skipped.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(skipped.Values, r => Assert.Equal("SeriesInstanceUID is not in the series selection", r));
        }

        [Fact]
        public void DryRun_StructSeries_NamesTheStructureSetTheSeriesWillUse()
        {
            var plan = DryRun("--series", "1.2.3.100.2", "--struct-series", "1.2.3.100.3");

            var series = Assert.Single(plan["series"]);
            Assert.Equal("1.2.3.100.3", (string)series["structure_set"]["series_uid"]);
        }

        [Fact]
        public void DryRun_Rois_ListsOnlyTheSelectedRois()
        {
            var plan = DryRun("--rois", "Target");

            var rois = plan["series"].ToDictionary(s => (string)s["series_uid"], s => s["rois"].Select(r => (string)r).ToList());
            Assert.Equal(new[] { "Target" }, rois["1.2.3.100.2"]); // Ring dropped
            Assert.Equal(new[] { "Target" }, rois["1.2.3.200.2"]);
            Assert.Empty(rois["1.2.3.400.2"]);                      // Body dropped
            Assert.Empty(plan["warnings"]);
        }

        [Fact]
        public void DryRun_RoisMatchingNothing_WarnsAsTheRunWouldFail()
        {
            var plan = DryRun("--rois", "Bladder");

            string warning = (string)Assert.Single(plan["warnings"]);
            Assert.StartsWith("WARNING: --rois matched no ROI in any of the 4 exported structure set(s)", warning);
            Assert.Contains("ROIs asked for [Bladder]", warning);
            Assert.Contains("[Body, Ring, Target]", warning);
        }

        /// <summary>
        /// The run then does what the plan said: no mask for any series, the manifest written with
        /// no ROI column, the reason in the JSON's errors, and exit 1.
        /// </summary>
        [Fact]
        public void Manifest_RoisMatchingNothing_RecordsWhyAndExits1()
        {
            var run = CliRun.Execute("--cohort-manifest", "--input", _cohort.Input,
                "--output", Path.Combine(_dir, "survey"), "--no-volumes", "--rois", "Bladder");

            Assert.True(run.ExitCode == 1, run.ToString());
            var result = JObject.Parse(run.Stdout);
            Assert.Empty((JArray)result["roi_columns"]);
            Assert.Contains(result["errors"], e => ((string)e["message"]).StartsWith("--rois matched no ROI", StringComparison.Ordinal));
            Assert.Contains("WARNING: --rois matched no ROI", run.Stderr);
        }

        private JObject DryRun(params string[] extra)
        {
            string output = Path.Combine(_dir, "planned");
            var run = CliRun.Execute(new[] { "--cohort-convert", "--input", _cohort.Input, "--output", output, "--dry-run" }
                .Concat(extra).ToArray());
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.False(Directory.Exists(output), run.ToString());
            return JObject.Parse(run.Stdout);
        }
    }
}
