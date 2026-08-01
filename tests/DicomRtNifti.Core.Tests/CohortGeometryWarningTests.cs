using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The non-uniform-Z warning fired on exactly the wrong paths. It was wired into the CLI's
    /// --forward and --image-forward — one series, one operator, watching — and into nothing else:
    /// --cohort-manifest, --cohort-convert and every GUI path converted mixed-gap series at exit 0
    /// with no diagnostic at all. The modes where a bad series is least likely to be noticed are
    /// the cohort ones, which is where a 40 mm slice jump turns into a cube of 96.4 cc next to
    /// 172.8 cc for its byte-identical siblings, months before anyone reads the numbers.
    ///
    /// These run the real cohort path, because "the probe can build a message" was already true
    /// before the fix; what was missing was anyone calling it.
    /// </summary>
    public class CohortGeometryWarningTests : IDisposable
    {
        private readonly string _dir;

        public CohortGeometryWarningTests()
        {
            _dir = DicomTestData.NewTempDir();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>Two 3 mm blocks with a 40 mm jump between them, the repro geometry.</summary>
        private string WriteNonUniformCohort()
        {
            string input = Path.Combine(_dir, "in");
            DicomTestData.WriteCtSeriesWithPixels(
                Path.Combine(input, "pat"), DicomTestData.NewUid(), DicomTestData.NewUid(),
                sliceZsMm: new[] { 0.0, 3.0, 6.0, 46.0, 49.0, 52.0 });
            return input;
        }

        private List<string> RunManifest(string input, double[] outputSpacing)
        {
            var scan = new DicomScannerService()
                .ScanFolderAsync(input, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            var options = new CohortExportOptions
            {
                InputRoot = input,
                OutputRoot = Path.Combine(_dir, "out"),
                OutputSpacing = outputSpacing,
            };

            var plan = CohortExportService.BuildPlan(scan, options, null);
            Assert.Single(plan.Series);

            var log = new CollectingProgress();
            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));
            var result = service.ComputeManifestAsync(
                    plan, options, null, log, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal(0, result.FailedCount);
            return log.Messages;
        }

        [Fact]
        public void CohortManifest_WarnsOnANonUniformSeries()
        {
            var log = RunManifest(WriteNonUniformCohort(), null);

            string warning = log.FirstOrDefault(m => m.Contains("non-uniform slice spacing"));
            Assert.NotNull(warning);
            Assert.Contains("3 mm", warning);     // smallest gap
            Assert.Contains("40 mm", warning);    // the jump
            Assert.Contains("10.4 mm", warning);  // what the flattened grid becomes: (52-0)/5
        }

        /// <summary>
        /// With a resample requested, the output is written on the target grid — so the warning
        /// must not claim the flattened spacing is what lands in the file, while still saying that
        /// resampling off a flattened grid does not recover the geometry.
        /// </summary>
        [Fact]
        public void CohortConvert_UnderTargetSpacing_DoesNotClaimTheFlattenedSpacingIsWritten()
        {
            var log = RunManifest(WriteNonUniformCohort(), new[] { 1.0, 1.0, 2.0 });

            string warning = log.FirstOrDefault(m => m.Contains("non-uniform slice spacing"));
            Assert.NotNull(warning);
            Assert.DoesNotContain("written at 10.4 mm", warning);
            Assert.Contains("written at the requested 1x1x2 mm", warning);
            Assert.Contains("does not recover the true slice positions", warning);
        }

        /// <summary>
        /// And the complement: a regular grid must stay quiet, or the message becomes noise on
        /// every one of the 400 patients and stops being read.
        /// </summary>
        [Fact]
        public void CohortManifest_IsQuietOnAUniformSeries()
        {
            string input = Path.Combine(_dir, "uniform");
            DicomTestData.WriteCtSeriesWithPixels(
                Path.Combine(input, "pat"), DicomTestData.NewUid(), DicomTestData.NewUid(),
                sliceCount: 6, sliceGapMm: 3.0);

            var log = RunManifest(input, null);

            Assert.DoesNotContain(log, m => m.Contains("non-uniform slice spacing"));
        }
    }
}
