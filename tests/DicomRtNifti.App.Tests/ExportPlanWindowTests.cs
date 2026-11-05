using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Core.Tests;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// W3-D.2: Convert Selected and Export Manifest Only show the export plan (the
    /// <c>BuildPlan</c> result, as the CLI's --dry-run prints it) before they start: the series to
    /// write and their folders, the structure set, masks and doses of each, and every skipped series
    /// with its reason. Cancelling writes nothing.
    /// </summary>
    public class ExportPlanWindowTests
    {
        [AvaloniaFact]
        public async Task Convert_selected_shows_the_plan_first_and_cancel_writes_nothing()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                CohortExportParityTests.Cohorts.ThreeSeriesForTheSeriesFilters(input);
                string output = Path.Combine(root.Path, "out");

                var (_, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, output);
                vm.RequireDose = true;
                vm.SeriesDescriptionFilter = "planning";
                int logStart = vm.LogText.Length;

                var plan = await PlanReview.CancelAsync(() => vm.ConvertSelectedCommand.ExecuteAsync(null), window =>
                {
                    var shown = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                    Assert.Contains("FILTER01/Planning CT", shown);
                    Assert.Contains("SeriesDescription does not contain 'planning'", shown);
                    Assert.Contains("no linked RTDOSE", shown);
                    Assert.Contains("Convert 1 series", window.GetVisualDescendants().OfType<Button>()
                        .Select(b => b.Content as string));
                    Screenshots.Capture(window, "15_dicom_to_nifti_export_plan");
                });

                Assert.Equal($"1 series to convert into {Path.GetFullPath(output)}", plan.Headline);
                Assert.Equal("Convert 1 series", plan.RunCaption);
                var series = Assert.Single(plan.Series);
                Assert.Equal("FILTER01/Planning CT", series.OutputDir);
                Assert.Equal("Structures: structures (2 ROIs) → 2 masks: Body, Target", series.Structures);
                Assert.Equal("Doses: 1 → Plan dose", series.Doses);
                Assert.Collection(plan.Skipped,
                    s =>
                    {
                        Assert.StartsWith("FILTER02/", s.Series);
                        Assert.Equal("SeriesDescription does not contain 'planning'", s.Reason);
                    },
                    s =>
                    {
                        Assert.StartsWith("FILTER03/", s.Series);
                        Assert.Equal("no linked RTDOSE", s.Reason);
                    });
                Assert.Contains("\"schema\": \"dicomrtnifti.cohort-plan/1\"", plan.Json);

                string log = vm.LogText.Substring(logStart);
                Assert.Contains("Conversion not started", log);
                Assert.Equal("Ready", vm.StatusText);
                Assert.False(Directory.Exists(output), "cancelling the plan created the output folder");
            }
        }

        [AvaloniaFact]
        public async Task A_plan_with_nothing_to_convert_cannot_be_run()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                DicomTestData.WritePatientWithStructureSet(input, "NODOSE_A", damagedContours: false);
                DicomTestData.WritePatientWithStructureSet(input, "NODOSE_B", damagedContours: false);
                string output = Path.Combine(root.Path, "out");

                var (_, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, output);
                vm.RequireDose = true;

                var plan = await PlanReview.CancelAsync(() => vm.ConvertSelectedCommand.ExecuteAsync(null), window =>
                    Assert.False(window.FindControl<Button>("RunButton").IsEffectivelyEnabled, "Run is enabled with nothing to run"));

                Assert.False(plan.CanRun);
                Assert.StartsWith("Nothing to convert", plan.Headline);
                Assert.Equal(2, plan.Skipped.Count);
                Assert.All(plan.Skipped, s => Assert.Equal("no linked RTDOSE", s.Reason));
                Assert.False(Directory.Exists(output));
            }
        }

        [AvaloniaFact]
        public async Task Export_manifest_only_surveys_the_plan_and_writes_only_its_manifest_with_roi_columns_by_name()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                CohortExportParityTests.Cohorts.TwoPatientsWithStructuresAndDose(input);
                string output = Path.Combine(root.Path, "out");

                var (_, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, output);
                var plan = await PlanReview.RunAsync(() => vm.ExportMetaDataCommand.ExecuteAsync(null));

                Assert.False(plan.IsConversion);
                Assert.Equal("Export manifest for 2 series", plan.RunCaption);
                Assert.EndsWith(MainViewModel.SurveyManifestFileName, plan.ManifestPath);
                Assert.All(plan.Series, s => Assert.Equal("Doses: 1 linked, not written", s.Doses));
                Assert.Equal("Metadata export complete.", vm.StatusText);

                // Nothing but the manifest, and its ROI columns are each series' ROIs by name, in
                // plan order (#92), as --cohort-manifest writes them.
                var written = Directory.GetFileSystemEntries(output, "*", SearchOption.AllDirectories);
                Assert.Equal(Path.Combine(output, MainViewModel.SurveyManifestFileName), Assert.Single(written));
                string header = File.ReadLines(Path.Combine(output, MainViewModel.SurveyManifestFileName)).First();
                Assert.Equal("PatientID,StudyUID,SeriesUID,SpacingX,SpacingY,SpacingZ,\"Avoid\",\"Body\",\"Target\",\"Cord\"", header);
                Assert.False(File.Exists(AnonymizationKeyLocation.DefaultFor(output)));
            }
        }
    }
}
