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
using DicomRtNifti.Core.Tests;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The forward window's inspector at each level of the tree, over one patient with two
    /// studies: a CT with the structure set drawn on it, and a study holding only a structure set
    /// (its images are elsewhere), which the tree cannot show. Selecting the patient, a study or
    /// the series fills the panel with that level's inspector; selecting nothing brings the hint
    /// back.
    /// </summary>
    public class InspectorLevelsWindowTests
    {
        [AvaloniaFact]
        public async Task Selecting_a_patient_study_or_series_shows_that_levels_inspector()
        {
            using (new UserSettingsGuard())
            using (var dicom = new TwoStudyCase())
            using (var launcher = new LauncherSession())
            {
                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                vm.InputFolder = dicom.InputFolder;
                vm.OutputFolder = dicom.OutputFolder;

                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Scan complete.", vm.StatusText);

                var hint = window.FindControl<TextBlock>("InspectorHint");
                Assert.NotNull(hint);
                Assert.Equal(InspectorViewModel.NoSelectionHint, hint.Text);
                Assert.True(hint.IsVisible);
                Assert.Equal("Inspector", vm.InspectorHeader);

                var patientRow = Assert.Single(vm.Patients);
                Assert.Equal(2, patientRow.Studies.Count);
                var imagingStudy = patientRow.Studies.Single(s => s.ImageSeries.Count == 1);
                var rtOnlyStudy = patientRow.Studies.Single(s => s.ImageSeries.Count == 0);
                var seriesRow = Assert.Single(imagingStudy.ImageSeries);

                // Patient row: the studies and what they hold, including the structure set the tree hides.
                vm.SelectedTreeItem = patientRow;
                Dispatcher.UIThread.RunJobs();
                var patient = Assert.IsType<PatientInspectorViewModel>(vm.SelectedInspector);
                Assert.Same(patientRow.Inspector, patient);
                Assert.False(vm.ShowInspectorHint);
                Assert.False(hint.IsVisible);
                Assert.Equal("Patient inspector", vm.InspectorHeader);
                Assert.Contains("Patient inspector", VisibleTexts(window));
                Assert.Equal("PAT001", patient.PatientId);
                Assert.Equal("Test^Patient", patient.PatientName);
                Assert.Equal(2, patient.StudyCount);
                Assert.Equal("1 CT", patient.ImageSeriesSummary);
                Assert.Equal(4, patient.ImageInstanceCount);
                Assert.Equal("1 linked, 1 unlinked", patient.RtStructText);
                Assert.Single(window.GetVisualDescendants().OfType<PatientInspectorView>());
                Assert.Contains("unlinked", VisibleTexts(window));

                await patient.EnsureTagsLoadedAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("", patient.TagStatus);
                Assert.Contains(patient.Tags, t => t.Section == "Patient" && t.Keyword == "PatientID" && t.Value == "PAT001");
                Assert.Contains(patient.Tags, t => t.Section == "Patient" && t.Keyword == "PatientName" && t.Value == "Test^Patient");
                Screenshots.Capture(window, "13_dicom_to_nifti_patient_inspector");

                // The imaging study: its series, and the structure set linked to it by the series UID it names.
                vm.SelectedTreeItem = imagingStudy;
                Dispatcher.UIThread.RunJobs();
                var study = Assert.IsType<StudyInspectorViewModel>(vm.SelectedInspector);
                Assert.Equal("Study inspector", vm.InspectorHeader);
                Assert.False(vm.ShowInspectorHint);
                Assert.Equal(dicom.ImagingStudyUid, study.StudyInstanceUid);
                Assert.Equal("1 CT", study.ImageSeriesSummary);
                Assert.Equal("1", study.FramesText);
                Assert.Equal("linked to [CT] CT series, by referenced series UID", Assert.Single(study.RtObjects).Link);
                Assert.False(study.HasUnlinkedObjects);
                Assert.Single(window.GetVisualDescendants().OfType<StudyInspectorView>());

                await study.EnsureTagsLoadedAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("", study.TagStatus);
                Assert.Contains(study.Tags, t => t.Section == "Study" && t.Keyword == "StudyInstanceUID" && t.Value == dicom.ImagingStudyUid);

                // The study the tree shows empty: its structure set is listed, badged and explained.
                vm.SelectedTreeItem = rtOnlyStudy;
                Dispatcher.UIThread.RunJobs();
                var orphanStudy = Assert.IsType<StudyInspectorViewModel>(vm.SelectedInspector);
                var orphan = Assert.Single(orphanStudy.RtObjects);
                Assert.Equal("RTSTRUCT", orphan.Kind);
                Assert.True(orphan.IsUnlinked);
                Assert.Equal("none", orphanStudy.ImageSeriesSummary);
                Assert.Contains(StudyInspectorViewModel.UnlinkedNote, VisibleTexts(window));

                // With no image series, the tags come from the structure set, which carries the study module too.
                await orphanStudy.EnsureTagsLoadedAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("", orphanStudy.TagStatus);
                Assert.Contains(orphanStudy.Tags, t => t.Keyword == "StudyInstanceUID" && t.Value == dicom.RtOnlyStudyUid);
                Screenshots.Capture(window, "14_dicom_to_nifti_study_inspector");

                // The series row still shows the series inspector.
                vm.SelectedTreeItem = seriesRow;
                Dispatcher.UIThread.RunJobs();
                var series = Assert.IsType<SeriesInspectorViewModel>(vm.SelectedInspector);
                Assert.Equal("Series inspector", vm.InspectorHeader);
                Assert.Equal("1 linked, by referenced series UID", series.RtStructLinkText);
                Assert.Single(window.GetVisualDescendants().OfType<SeriesInspectorView>());
                await series.EnsureTagsLoadedAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("", series.TagStatus);

                // Nothing selected: the hint again.
                vm.SelectedTreeItem = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Null(vm.SelectedInspector);
                Assert.True(vm.ShowInspectorHint);
                Assert.True(hint.IsVisible);
                Assert.Equal("Inspector", vm.InspectorHeader);
                Assert.Empty(window.GetVisualDescendants().OfType<PatientInspectorView>());
                Assert.Empty(window.GetVisualDescendants().OfType<StudyInspectorView>());
                Assert.Empty(window.GetVisualDescendants().OfType<SeriesInspectorView>());
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

        /// <summary>
        /// Patient PAT001 with two studies: a 4-slice CT plus an RTSTRUCT that names it by series
        /// UID, and a study holding only an RTSTRUCT on another frame of reference, synthesized with
        /// the Core tests' fo-dicom writers.
        /// </summary>
        private sealed class TwoStudyCase : IDisposable
        {
            public TwoStudyCase()
            {
                Root = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));
                InputFolder = Path.Combine(Root, "dicom");
                OutputFolder = Path.Combine(Root, "out");
                Directory.CreateDirectory(OutputFolder);

                ImagingStudyUid = DicomTestData.NewUid();
                RtOnlyStudyUid = DicomTestData.NewUid();
                string frameUid = DicomTestData.NewUid();
                string ctSeriesUid = DicomTestData.NewUid();
                var target = new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                };

                DicomTestData.WriteCtSeriesWithPixels(Path.Combine(InputFolder, "ct"), ctSeriesUid, frameUid,
                    size: 16, studyUid: ImagingStudyUid);
                DicomTestData.WriteRtStructWithContours(InputFolder, "rtstruct.dcm", ImagingStudyUid, DicomTestData.NewUid(), frameUid,
                    target, referencedSeriesUid: ctSeriesUid);
                DicomTestData.WriteRtStructWithContours(InputFolder, "rtstruct_elsewhere.dcm", RtOnlyStudyUid, DicomTestData.NewUid(),
                    DicomTestData.NewUid(), target);
            }

            public string Root { get; }
            public string InputFolder { get; }
            public string OutputFolder { get; }
            public string ImagingStudyUid { get; }
            public string RtOnlyStudyUid { get; }

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
