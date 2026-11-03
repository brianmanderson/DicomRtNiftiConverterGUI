using System;
using System.Collections.Generic;
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
    /// <see cref="CohortExportOptions.RoiSelection"/> reaches the files: the conversion writes a
    /// mask for each selected ROI and no other, under the canonical name an association gives it,
    /// and the manifest's ROI columns follow; a selection that matches nothing is reported as
    /// --only-associated-rois' is. One patient, one CT, a structure set with Target and Ring.
    /// </summary>
    public class CohortRoiSelectionRunTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();
        private readonly string _input;

        public CohortRoiSelectionRunTests()
        {
            _input = Path.Combine(_dir, "cohort");
            string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(_input, DicomTestData.NewUid(), frame,
                sliceCount: 4, size: 16, studyUid: study, patientId: "PAT_A");
            DicomTestData.WriteRtStructWithContours(_input, "rtstruct.dcm", study, DicomTestData.NewUid(), frame,
                new[]
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                    new KeyValuePair<string, double[]>("Ring", DicomTestData.SquareContour(5, 12, 2)),
                },
                patientId: "PAT_A");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void WithoutASelection_EveryRoiIsExported()
        {
            var (result, masks) = Convert("all", new CohortExportOptions());

            Assert.Equal(new[] { "Ring.nii.gz", "Target.nii.gz" }, masks);
            Assert.Equal(new[] { "Ring", "Target" }, result.RoiColumns.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void ASelection_ExportsOnlyTheSelectedRois()
        {
            var (result, masks) = Convert("selected", new CohortExportOptions { RoiSelection = { "target" } });

            // The selected spelling names the mask: "target" has no association, so it matches
            // itself (case-insensitively) and is the canonical name.
            Assert.Equal(new[] { "target.nii.gz" }, masks);
            Assert.Equal(new[] { "target" }, result.RoiColumns);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ASelectedAssociation_ExportsUnderItsCanonicalName()
        {
            var options = new CohortExportOptions
            {
                Associations = { new RoiAssociation { CanonicalName = "Tumour", Aliases = { "Target", "GTV" } } },
                RoiSelection = { "Tumour" },
            };

            var (result, masks) = Convert("associated", options);

            Assert.Equal(new[] { "Tumour.nii.gz" }, masks);
            Assert.True(Assert.Single(Assert.Single(result.Series).Masks).VolumeCc > 0);
        }

        [Fact]
        public async Task TheSurvey_TakesTheSelectionToo()
        {
            var options = new CohortExportOptions { RoiSelection = { "Ring" }, ComputeVolumes = false };
            var (plan, service) = Prepare("survey", options);

            var result = await service.ComputeManifestAsync(plan, options, null, null, CancellationToken.None);

            Assert.Equal(new[] { "Ring" }, result.RoiColumns);
        }

        [Fact]
        public void ASelectionMatchingNothing_IsAnError_AsWithOnlyAssociatedRois()
        {
            var options = new CohortExportOptions { RoiSelection = { "Bladder" } };

            var (result, masks) = Convert("nothing", options);

            Assert.Empty(masks);
            string message = Assert.Single(result.Errors).Message;
            Assert.Equal(
                "--rois matched no ROI in any of the 1 exported structure set(s), so no mask was exported. " +
                "ROIs asked for [Bladder]; the structure sets contain [Ring, Target]. Fix the names given " +
                "to --rois, or the aliases of the associations they name.", message);
            Assert.Equal(1, result.SucceededCount);
        }

        [Fact]
        public void OnlyAssociatedRoisMatchingNothing_KeepsItsOwnMessage()
        {
            var options = new CohortExportOptions
            {
                Associations = { new RoiAssociation { CanonicalName = "Liver", Aliases = { "liver" } } },
                OnlyAssociatedRois = true,
            };

            var (result, _) = Convert("associations", options);

            Assert.StartsWith("--only-associated-rois matched no ROI in any of the 1 exported structure set(s)",
                Assert.Single(result.Errors).Message);
        }

        private (CohortExportResult, List<string>) Convert(string output, CohortExportOptions options)
        {
            var (plan, service) = Prepare(output, options);
            var result = service.ExecuteAsync(plan, options, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            string masksDir = Path.Combine(options.OutputRoot, plan.Series[0].RelativeOutputDir, "masks");
            var masks = Directory.Exists(masksDir)
                ? Directory.GetFiles(masksDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()
                : new List<string>();
            return (result, masks);
        }

        private (CohortExportPlan, CohortExportService) Prepare(string output, CohortExportOptions options)
        {
            options.InputRoot = _input;
            options.OutputRoot = Path.Combine(_dir, output);
            var scan = new DicomScannerService()
                .ScanFolderAsync(_input, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var plan = CohortExportService.BuildPlan(scan, options, null);
            Assert.Single(plan.Series);
            return (plan, new CohortExportService(new NiftiConversionService(new RtStructMaskService())));
        }
    }
}
