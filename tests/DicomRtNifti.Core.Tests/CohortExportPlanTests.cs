using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Cohort planning: which series get exported, what they are named, and where they land.
    /// Planning is pure, so these run without the SimpleITK native.
    ///
    /// The fixture mirrors Pancreatic-CT-CBCT-SEG: one study per patient holding a larger
    /// planning CT and a shorter aligned CBCT that shares its frame of reference, one structure
    /// set per image series, and one dose.
    /// </summary>
    public class CohortExportPlanTests : IDisposable
    {
        private readonly string _dir;
        private readonly DicomScanResult _scan;

        public CohortExportPlanTests()
        {
            _dir = DicomTestData.NewTempDir();

            foreach (var pid in new[] { "PANC_001", "PANC_002" })
            {
                string study = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();
                string planningCt = DicomTestData.NewUid();
                string cbct = DicomTestData.NewUid();

                for (int i = 0; i < 20; i++)
                    DicomTestData.WriteImageSlice(_dir, $"{pid}_pct_{i:00}.dcm", "CT", pid, study,
                        planningCt, frame, i * 3.0);
                for (int i = 0; i < 8; i++)
                    DicomTestData.WriteImageSlice(_dir, $"{pid}_cb_{i:00}.dcm", "CT", pid, study,
                        cbct, frame, i * 3.0);

                DicomTestData.WriteRtStruct(_dir, $"{pid}_rs_pct.dcm", pid, study,
                    DicomTestData.NewUid(), frame, "BSPC_SDPC",
                    new[] { "Pancreas", "Duodenum" }, referencedSeriesUid: planningCt,
                    seriesDescription: "BSPC_LL_LR_ROI_SDPC");
                DicomTestData.WriteRtStruct(_dir, $"{pid}_rs_cb.dcm", pid, study,
                    DicomTestData.NewUid(), frame, "BSCB_SDCB",
                    new[] { "Pancreas" }, referencedSeriesUid: cbct,
                    seriesDescription: "BSCB_LL_LR_SDCB");
                DicomTestData.WriteRtDose(_dir, $"{pid}_dose.dcm", pid, study,
                    DicomTestData.NewUid(), frame);
            }

            _scan = new DicomScannerService()
                .ScanFolderAsync(_dir, null, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private AnonymizationService NewAnon(string salt = "test-salt") =>
            new AnonymizationService(Path.Combine(_dir, "key.json"), salt);

        [Fact]
        public void WithoutSelection_EveryImageSeriesIsPlanned()
        {
            var plan = CohortExportService.BuildPlan(_scan, new CohortExportOptions(), null);

            // Two patients, two image series each.
            Assert.Equal(4, plan.Series.Count);
        }

        [Fact]
        public void PreferLargestSeries_PicksThePlanningCt()
        {
            var plan = CohortExportService.BuildPlan(
                _scan, new CohortExportOptions { PreferLargestSeries = true }, null);

            Assert.Equal(2, plan.Series.Count);
            Assert.All(plan.Series, s => Assert.Equal(20, s.Image.FilePaths.Count));
            Assert.All(plan.Skipped, s => Assert.Contains("not the largest series", s.Reason));
        }

        [Fact]
        public void SeriesDescriptionFilter_KeepsOnlyMatches()
        {
            // WriteImageSlice labels every series "CT series", so a non-matching needle should
            // drop everything and say why.
            var plan = CohortExportService.BuildPlan(
                _scan, new CohortExportOptions { SeriesDescriptionFilter = "CBCT-only" }, null);

            Assert.Empty(plan.Series);
            Assert.Equal(4, plan.Skipped.Count);
            Assert.All(plan.Skipped, s => Assert.Contains("does not contain", s.Reason));
        }

        [Fact]
        public void PatientFilter_RestrictsToNamedPatients()
        {
            var plan = CohortExportService.BuildPlan(
                _scan,
                new CohortExportOptions { PatientIds = { "PANC_002" }, PreferLargestSeries = true },
                null);

            var series = Assert.Single(plan.Series);
            Assert.Equal("PANC_002", series.PatientId);
        }

        [Fact]
        public void RequireDose_KeepsSeriesThatShareTheDoseFrameOfReference()
        {
            // Both image series in each study share the dose's frame of reference, so the dose
            // links to both and RequireDose excludes nothing. This is the property that lets
            // --struct-description select the planning CT without orphaning its dose.
            var plan = CohortExportService.BuildPlan(
                _scan, new CohortExportOptions { RequireDose = true }, null);

            Assert.Equal(4, plan.Series.Count);
            Assert.Empty(plan.Skipped);
            Assert.All(plan.Series, s => Assert.NotEmpty(s.RtDoses));
        }

        [Fact]
        public void StructDescriptionFilter_SelectsTheSeriesAndTheMatchingStructureSet()
        {
            // The real motivation: sibling series that tie on every other identifier are told
            // apart by the structure set drawn on them.
            var plan = CohortExportService.BuildPlan(
                _scan,
                new CohortExportOptions { StructDescriptionFilter = "BSPC", RequireDose = true },
                null);

            Assert.Equal(2, plan.Series.Count);
            Assert.All(plan.Series, s =>
            {
                Assert.Contains("BSPC", s.RtStruct.SeriesDescription);
                // The planning structure set carries an ROI the CBCT set does not.
                Assert.Contains("Duodenum", s.RtStruct.RoiNames);
                Assert.NotEmpty(s.RtDoses);
            });
            Assert.All(plan.Skipped, s => Assert.Contains("no linked RTSTRUCT whose description", s.Reason));
        }

        [Fact]
        public void ReadableLayout_UsesPatientIdAndSeriesLabel()
        {
            var plan = CohortExportService.BuildPlan(
                _scan, new CohortExportOptions { PreferLargestSeries = true }, null);

            var series = plan.Series.First(s => s.PatientId == "PANC_001");
            Assert.StartsWith("PANC_001/", series.RelativeOutputDir);
            Assert.Equal("PANC_001", series.ExportPatientId);
        }

        [Fact]
        public void AnonymizedLayout_IsAThreeLevelHashTriple()
        {
            var anon = NewAnon();
            var plan = CohortExportService.BuildPlan(
                _scan,
                new CohortExportOptions { PreferLargestSeries = true, Anonymize = true },
                anon);

            var series = plan.Series[0];
            var segments = series.RelativeOutputDir.Split('/');

            Assert.Equal(3, segments.Length);
            Assert.DoesNotContain("PANC_", series.RelativeOutputDir);
            Assert.Equal(segments[0], series.ExportPatientId);
            Assert.NotEqual(series.PatientId, series.ExportPatientId);
        }

        [Fact]
        public void SameSalt_ReproducesTheSameFolderNames()
        {
            string First()
            {
                var plan = CohortExportService.BuildPlan(
                    _scan,
                    new CohortExportOptions { PreferLargestSeries = true, Anonymize = true },
                    NewAnon("stable-salt"));
                return plan.Series[0].RelativeOutputDir;
            }

            // Growing a cohort depends on this: re-pulling the same patient must land in the
            // same folder rather than creating a second copy under a new hash.
            Assert.Equal(First(), First());
        }

        [Fact]
        public void DifferentSalt_ProducesDifferentFolderNames()
        {
            var a = CohortExportService.BuildPlan(_scan,
                new CohortExportOptions { PreferLargestSeries = true, Anonymize = true },
                NewAnon("salt-a")).Series[0].RelativeOutputDir;

            var b = CohortExportService.BuildPlan(_scan,
                new CohortExportOptions { PreferLargestSeries = true, Anonymize = true },
                new AnonymizationService(Path.Combine(_dir, "key_b.json"), "salt-b"))
                .Series[0].RelativeOutputDir;

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void SkippedSeries_CarryHashedIdentifiers_WhenAnonymizing()
        {
            // Regression: skipped entries are serialized into the cohort JSON next to the
            // exported ones, so an unhashed PatientID here would leak PHI into a notebook's
            // committed cell output even though the export itself was anonymized.
            var plan = CohortExportService.BuildPlan(
                _scan,
                new CohortExportOptions { PreferLargestSeries = true, Anonymize = true },
                NewAnon());

            Assert.NotEmpty(plan.Skipped);
            Assert.All(plan.Skipped, s =>
            {
                Assert.DoesNotContain("PANC_", s.PatientId);
                Assert.NotEqual("", s.PatientId);
            });
        }

        [Theory]
        [InlineData("1.0,1.0,3.0", true)]
        [InlineData(" 0.98 , 0.98 , 3 ", true)]
        [InlineData("1,2", false)]
        [InlineData("a,b,c", false)]
        [InlineData("0,1,1", false)]
        [InlineData("-1,1,1", false)]
        [InlineData("", false)]
        public void TryParseSpacing_AcceptsThreePositiveNumbersOnly(string text, bool expected)
        {
            bool ok = CohortExportOptions.TryParseSpacing(text, out var spacing, out string error);

            Assert.Equal(expected, ok);
            if (ok)
            {
                Assert.Equal(3, spacing.Length);
                Assert.All(spacing, v => Assert.True(v > 0));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(error));
            }
        }

        [Fact]
        public void ParseKeywordList_TrimsAndDropsEmpties()
        {
            Assert.Equal(
                new[] { "PatientAge", "@MaxDose" },
                CohortExportOptions.ParseKeywordList(" PatientAge , , @MaxDose "));
            Assert.Empty(CohortExportOptions.ParseKeywordList(null));
        }
    }
}
