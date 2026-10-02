using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using FellowOakDicom;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The user's path end to end: open DICOM -> NIfTI from the launcher, scan a folder holding a
    /// CT series and its RTSTRUCT, export image + masks; then open NIfTI -> DICOM, point it at
    /// that export, and convert it back into a DICOM image series plus an RT-STRUCT. Every step
    /// goes through the real view-models and Core services (SimpleITK included), driven exactly
    /// as the bound controls drive them, with no folder dialog.
    /// </summary>
    public class WorkflowRoundTripTests
    {
        private readonly ITestOutputHelper _out;

        public WorkflowRoundTripTests(ITestOutputHelper output) => _out = output;

        [AvaloniaFact]
        public async Task Forward_export_then_reverse_conversion_round_trips_through_the_gui()
        {
            using (new UserSettingsGuard())
            using (var testCase = new TestCase())
            using (var launcher = new LauncherSession())
            {
                _out.WriteLine($"input: {testCase.Source} ({testCase.SliceCount} slices, {testCase.RoiNames.Count} ROIs)");

                string seriesFolder = await ForwardExportAsync(launcher, testCase);
                await ReverseConvertAsync(launcher, testCase, seriesFolder);
            }
        }

        private async Task<string> ForwardExportAsync(LauncherSession launcher, TestCase testCase)
        {
            var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
            try
            {
                // Every option is set explicitly so a stored preference can never change the tree.
                vm.InputFolder = testCase.InputFolder;
                vm.OutputFolder = testCase.OutputFolder;
                vm.ExportImages = true;
                vm.IncludeStructures = true;
                vm.IncludeDose = false;
                vm.OnlyExportSpecificRois = false;
                vm.SpecifyOutputSpacing = false;
                vm.AnonymizeExport = false;
                vm.ExportDicomMetadata = false;

                Assert.True(vm.ScanCommand.CanExecute(null));
                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Scan complete.", vm.StatusText);
                Assert.DoesNotContain("Scan error", vm.LogText);
                Assert.Single(vm.Patients);
                var series = Assert.Single(Assert.Single(vm.Patients[0].Studies).ImageSeries);
                Assert.True(series.HasLinkedRtStruct, "the RTSTRUCT was not linked to the CT series");
                Assert.Equal(testCase.RoiNames.OrderBy(n => n).ToList(), vm.AllDiscoveredRoiNames.OrderBy(n => n).ToList());

                vm.AllPatientsSelected = true;
                Assert.True(vm.ConvertSelectedCommand.CanExecute(null));
                Screenshots.Capture(window, "06_dicom_to_nifti_scanned");

                await vm.ConvertSelectedCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                _out.WriteLine(vm.LogText);
                Assert.Equal("Conversion complete.", vm.StatusText);
                Assert.DoesNotContain("Conversion error", vm.LogText);
                Assert.Contains($"Conversion complete. 1 series exported to {testCase.OutputFolder}", vm.LogText);
                Assert.True(File.Exists(Path.Combine(testCase.OutputFolder, "export_manifest.csv")), "export_manifest.csv missing");

                string seriesFolder = testCase.FindExportedSeriesFolder();
                var masks = Directory.GetFiles(Path.Combine(seriesFolder, "masks"), "*.nii.gz");
                Assert.Equal(testCase.RoiNames.Count, masks.Length);
                Assert.Equal(
                    testCase.RoiNames.Select(n => n.ToLowerInvariant()).OrderBy(n => n).ToList(),
                    masks.Select(m => Path.GetFileName(m).Replace(".nii.gz", "").ToLowerInvariant()).OrderBy(n => n).ToList());

                Screenshots.Capture(window, "07_dicom_to_nifti_converted");
                return seriesFolder;
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private async Task ReverseConvertAsync(LauncherSession launcher, TestCase testCase, string seriesFolder)
        {
            var window = launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
            try
            {
                vm.RootFolder = seriesFolder;
                vm.ConvertImage = true;
                vm.ConvertStructures = true;
                vm.ConvertDoses = false;

                Assert.True(vm.ScanRootFolderCommand.CanExecute(null));
                vm.ScanRootFolderCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                var job = Assert.Single(vm.DiscoveredJobs);
                Assert.True(job.HasImage);
                Assert.Equal(testCase.RoiNames.Count, job.MaskCount);
                Assert.Equal(0, job.DoseCount);
                Assert.StartsWith("Found 1 folder(s)", vm.StatusText);
                Screenshots.Capture(window, "08_nifti_to_dicom_scanned");

                Assert.True(vm.ConvertCommand.CanExecute(null));
                await vm.ConvertCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                _out.WriteLine($"{job.FolderDisplayName}: {job.Status}");
                Assert.Equal("Done. 1 succeeded, 0 failed.", vm.StatusText);
                Assert.StartsWith("OK —", job.Status);

                // A DICOM image series was synthesized from image.nii.gz, one file per slice ...
                var slices = Directory.GetFiles(seriesFolder, "image_*.dcm");
                Assert.Equal(testCase.SliceCount, slices.Length);

                // ... and the masks became one RT-STRUCT referencing it, one ROI per mask.
                Assert.True(File.Exists(job.OutputPath), $"RT-STRUCT not written at {job.OutputPath}");
                var rtstruct = DicomFile.Open(job.OutputPath).Dataset;
                Assert.Equal("RTSTRUCT", rtstruct.GetSingleValue<string>(DicomTag.Modality));
                var rois = rtstruct.GetSequence(DicomTag.StructureSetROISequence).Items
                    .Select(i => i.GetSingleValue<string>(DicomTag.ROIName)).OrderBy(n => n).ToList();
                Assert.Equal(testCase.RoiNames.Count, rois.Count);

                string imageSeriesUid = DicomFile.Open(slices[0]).Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);
                string referencedSeriesUid = rtstruct
                    .GetSequence(DicomTag.ReferencedFrameOfReferenceSequence).Items[0]
                    .GetSequence(DicomTag.RTReferencedStudySequence).Items[0]
                    .GetSequence(DicomTag.RTReferencedSeriesSequence).Items[0]
                    .GetSingleValue<string>(DicomTag.SeriesInstanceUID);
                Assert.Equal(imageSeriesUid, referencedSeriesUid);

                Screenshots.Capture(window, "09_nifti_to_dicom_converted");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
