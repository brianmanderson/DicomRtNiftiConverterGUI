using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Core.Tests;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The forward window over a CT whose slice gaps are mixed (1, 1, 3 mm): the scan badges it
    /// in the tree and says so in the log, the series inspector shows the gaps, the link rule and
    /// the tag grid, and Export Manifest Only logs the same warning Convert Selected does.
    /// </summary>
    public class SeriesInspectorWindowTests
    {
        [AvaloniaFact]
        public async Task Scanning_a_non_uniform_z_series_badges_it_and_the_inspector_explains_it()
        {
            using (new UserSettingsGuard())
            using (var dicom = new NonUniformCtCase())
            using (var launcher = new LauncherSession())
            {
                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                vm.InputFolder = dicom.InputFolder;
                vm.OutputFolder = dicom.OutputFolder;
                vm.IncludeStructures = true;
                vm.OnlyExportSpecificRois = false;
                vm.SpecifyOutputSpacing = false;
                vm.AnonymizeExport = false;

                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Scan complete.", vm.StatusText);

                var patient = Assert.Single(vm.Patients);
                var series = Assert.Single(Assert.Single(patient.Studies).ImageSeries);
                Assert.True(series.IsNonUniformZ);
                Assert.Equal(1, patient.NonUniformSeriesCount);
                Assert.Contains("1 series has non-uniform slice spacing", vm.LogText);

                // The patient row is collapsed after a scan; its rolled-up badge is what shows.
                Assert.Contains("non-uniform Z", VisibleTexts(window));

                // Expand to the series row: its own badge is there too.
                ExpandAll(window);
                Assert.True(VisibleTexts(window).Count(t => t == "non-uniform Z") >= 2,
                    "expected the badge on both the patient row and the series row");

                // Select the series, as a click on its row does: the inspector fills in.
                vm.SelectedTreeItem = series;
                Dispatcher.UIThread.RunJobs();
                var inspector = vm.SelectedInspector;
                Assert.NotNull(inspector);
                Assert.False(vm.ShowInspectorHint);
                Assert.True(inspector.IsNonUniformZ);
                Assert.Equal("1, 3 mm", inspector.DistinctGapsText);
                Assert.Equal("1 mm", inspector.SliceSpacingText);
                Assert.Equal("5 mm (z 0 to 5)", inspector.ExtentText);
                Assert.Equal("1 × 1 mm", inspector.PixelSpacingText);
                Assert.Equal(dicom.FrameUid, inspector.FrameOfReferenceUid);
                Assert.Equal("1 linked, by frame of reference UID", inspector.RtStructLinkText);

                await inspector.EnsureTagsLoadedAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("", inspector.TagStatus);
                Assert.Contains(inspector.Tags, t => t.Section == "Image" && t.Keyword == "PixelSpacing" && t.Value == "1\\1");
                Assert.Contains(inspector.Tags, t => t.Section == "Image" && t.Keyword == "FrameOfReferenceUID" && t.Value == dicom.FrameUid);
                Assert.Contains(inspector.Tags, t => t.Section == "Structure set" && t.Keyword == "Modality" && t.Value == "RTSTRUCT");

                var view = window.GetVisualDescendants().OfType<SeriesInspectorView>().Single();
                Assert.Contains("non-uniform Z", view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                Screenshots.Capture(window, "10_dicom_to_nifti_inspector");

                // Export Manifest Only now warns exactly as Convert Selected does.
                string expected;
                Assert.True(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(series.Model, null, out expected));
                await vm.ExportMetaDataCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Metadata export complete.", vm.StatusText);
                Assert.Contains("  " + expected, vm.LogText);
                Assert.True(File.Exists(Path.Combine(dicom.OutputFolder, "export_manifest_meta.csv")));
            }
        }

        private static List<string> VisibleTexts(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            return window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text != null)
                .Select(t => t.Text)
                .ToList();
        }

        private static void ExpandAll(Window window)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>().ToList())
                    item.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>
        /// A 16x16 CT at z = 0, 1, 2, 5 mm (gaps 1, 1, 3) with an RTSTRUCT that names its frame of
        /// reference only, synthesized with the Core tests' fo-dicom writers.
        /// </summary>
        private sealed class NonUniformCtCase : IDisposable
        {
            public NonUniformCtCase()
            {
                Root = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));
                InputFolder = Path.Combine(Root, "dicom");
                OutputFolder = Path.Combine(Root, "out");
                Directory.CreateDirectory(OutputFolder);

                string studyUid = DicomTestData.NewUid();
                FrameUid = DicomTestData.NewUid();
                DicomTestData.WriteCtSeriesWithPixels(InputFolder, DicomTestData.NewUid(), FrameUid,
                    size: 16, studyUid: studyUid, sliceZsMm: new[] { 0.0, 1.0, 2.0, 5.0 });
                DicomTestData.WriteRtStructWithContours(InputFolder, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), FrameUid,
                    new List<KeyValuePair<string, double[]>>
                    {
                        new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                    });
            }

            public string Root { get; }
            public string InputFolder { get; }
            public string OutputFolder { get; }
            public string FrameUid { get; }

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
