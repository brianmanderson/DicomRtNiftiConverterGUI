using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Tests;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// Three patients, the middle one with a structure set whose contour data is damaged: the scan
    /// lists all three, and Convert Selected and Export Manifest Only each report the damaged
    /// series and carry on with the rest instead of ending the run at it.
    /// </summary>
    public class FailedSeriesTests
    {
        [AvaloniaFact]
        public async Task Convert_selected_reports_a_failed_series_and_exports_the_others()
        {
            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                string root = NewRoot();
                try
                {
                    var (window, vm) = await OpenScannedAsync(launcher, root);
                    vm.ExportImages = true;
                    vm.IncludeStructures = true;
                    await PlanReview.RunAsync(() => vm.ConvertSelectedCommand.ExecuteAsync(null));
                    Dispatcher.UIThread.RunJobs();

                    string output = Path.Combine(root, "out");
                    Assert.Equal("Conversion complete; 1 series failed.", vm.StatusText);
                    Assert.Contains("FAILED PAT_B/", vm.LogText);
                    Assert.Contains($"Conversion complete. 2 series exported to {output}; 1 failed", vm.LogText);
                    Assert.DoesNotContain("Conversion error", vm.LogText);
                    foreach (var patient in new[] { "PAT_A", "PAT_C" })
                    {
                        var masks = Directory.GetFiles(Path.Combine(output, patient), "*.nii.gz", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "masks").ToList();
                        Assert.Equal("target", Path.GetFileName(Assert.Single(masks)).Replace(".nii.gz", "").ToLowerInvariant());
                    }
                    AssertManifestRows(Path.Combine(output, "export_manifest.csv"));

                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
                finally { Delete(root); }
            }
        }

        [AvaloniaFact]
        public async Task Export_manifest_only_reports_a_failed_series_and_surveys_the_others()
        {
            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                string root = NewRoot();
                try
                {
                    var (window, vm) = await OpenScannedAsync(launcher, root);
                    vm.IncludeStructures = true;
                    await PlanReview.RunAsync(() => vm.ExportMetaDataCommand.ExecuteAsync(null));
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal("Metadata export complete; 1 series failed.", vm.StatusText);
                    Assert.Contains("FAILED PAT_B/", vm.LogText);
                    Assert.Contains("Metadata export complete. 2 series processed. 1 failed", vm.LogText);
                    Assert.DoesNotContain("Metadata export error", vm.LogText);
                    AssertManifestRows(Path.Combine(root, "out", "export_manifest_meta.csv"));

                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
                finally { Delete(root); }
            }
        }

        private static string NewRoot() =>
            Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));

        private static async Task<(DicomToNiftiWindow, MainViewModel)> OpenScannedAsync(LauncherSession launcher, string root)
        {
            string input = Path.Combine(root, "dicom");
            DicomTestData.WritePatientWithStructureSet(input, "PAT_A", damagedContours: false);
            DicomTestData.WritePatientWithStructureSet(input, "PAT_B", damagedContours: true);
            DicomTestData.WritePatientWithStructureSet(input, "PAT_C", damagedContours: false);
            Directory.CreateDirectory(Path.Combine(root, "out"));

            var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
            vm.InputFolder = input;
            vm.OutputFolder = Path.Combine(root, "out");
            vm.ExportImages = false;
            vm.IncludeStructures = false;
            vm.IncludeDose = false;
            vm.OnlyExportSpecificRois = false;
            vm.SpecifyOutputSpacing = false;
            vm.AnonymizeExport = false;
            vm.ExportDicomMetadata = false;

            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Scan complete.", vm.StatusText);
            Assert.Equal(3, vm.Patients.Count);
            Assert.All(vm.Patients, p => Assert.True(Assert.Single(Assert.Single(p.Studies).ImageSeries).HasLinkedRtStruct));
            vm.AllPatientsSelected = true;
            return (window, vm);
        }

        private static void AssertManifestRows(string manifestPath)
        {
            var lines = File.ReadAllLines(manifestPath);
            Assert.Contains(lines, l => l.StartsWith("\"PAT_A\",", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("\"PAT_C\",", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.StartsWith("\"PAT_B\",", StringComparison.Ordinal));
        }

        private static void Delete(string root)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
