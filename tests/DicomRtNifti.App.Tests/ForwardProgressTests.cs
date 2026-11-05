using System;
using System.Collections.Generic;
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
    /// W3-D.2: the progress bar and status line follow Core's structured events: a run's
    /// <c>CohortProgress</c> (series i of n, in plan order, failures counted), and a scan's
    /// <c>ScanProgress</c> (files read of files found).
    /// </summary>
    public class ForwardProgressTests
    {
        [AvaloniaFact]
        public async Task Convert_selected_moves_the_bar_per_series_in_plan_order_counting_a_failure()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                DicomTestData.WritePatientWithStructureSet(input, "PAT_A", damagedContours: false);
                DicomTestData.WritePatientWithStructureSet(input, "PAT_B", damagedContours: true);
                DicomTestData.WritePatientWithStructureSet(input, "PAT_C", damagedContours: false);
                string output = root.Folder("out");

                var (_, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, output);
                var values = new List<double>();
                var statuses = new List<string>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.ProgressValue)) values.Add(vm.ProgressValue);
                    if (e.PropertyName == nameof(MainViewModel.StatusText)) statuses.Add(vm.StatusText);
                };

                await PlanReview.RunAsync(() => vm.ConvertSelectedCommand.ExecuteAsync(null));

                Assert.Equal(0.0, values.First());
                Assert.Equal(100.0, values.Last());
                for (int i = 1; i < values.Count; i++)
                    Assert.True(values[i] >= values[i - 1], $"the bar went back: {string.Join(", ", values)}");
                Assert.Contains(values, v => Math.Abs(v - 100.0 / 3) < 1e-9);
                Assert.Contains(values, v => Math.Abs(v - 200.0 / 3) < 1e-9);

                var started = statuses.Where(s => s.StartsWith("Converting series", StringComparison.Ordinal)).ToList();
                Assert.Collection(started,
                    s => Assert.StartsWith("Converting series 1 of 3: PAT_A/", s),
                    s => Assert.StartsWith("Converting series 2 of 3: PAT_B/", s),
                    s => Assert.StartsWith("Converting series 3 of 3: PAT_C/", s));
                Assert.Equal("Conversion complete; 1 series failed.", vm.StatusText);
            }
        }

        [AvaloniaFact]
        public async Task The_scan_moves_the_bar_as_headers_are_read()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                // 120 slices: the scanner reports its counts every 50 reads and once at the end.
                string input = root.Folder("dicom");
                for (int p = 0; p < 2; p++)
                    DicomTestData.WriteCtSeriesWithPixels(Path.Combine(input, "SCAN0" + p), DicomTestData.NewUid(),
                        DicomTestData.NewUid(), sliceCount: 60, size: 8, patientId: "SCAN0" + p);

                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                vm.InputFolder = input;
                var values = new List<double>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.ProgressValue)) values.Add(vm.ProgressValue);
                };

                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Scan complete.", vm.StatusText);
                Assert.Equal(2, vm.Patients.Count);
                // Reset, the counts at 50 and 100 reads and the final one, then the scan's own 100.
                Assert.True(values.Count >= 4, $"the bar moved only at the start and end: {string.Join(", ", values)}");
                Assert.All(values, v => Assert.InRange(v, 0.0, 100.0));
                Assert.Equal(100.0, values.Last());
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
