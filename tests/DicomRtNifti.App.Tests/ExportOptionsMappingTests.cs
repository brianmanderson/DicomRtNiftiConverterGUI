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
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// W3-D.2: the forward window's state is mapped, and only mapped, to the
    /// <c>CohortExportOptions</c> it runs (MainViewModel.ExportOptionsFor): the ticked patients and
    /// series, the structure set picked on a series row, the series-selection controls of Export
    /// Options, and "Limit export to selected ROIs" with W4-C's GUI mapping (a stale selection is
    /// not passed when the filter is off).
    /// </summary>
    public class ExportOptionsMappingTests
    {
        [AvaloniaFact]
        public async Task The_window_maps_its_controls_to_the_cohort_options_it_runs()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                CohortExportParityTests.Cohorts.TwoPatientsWithStructuresAndDose(input);   // PARITY01, PARITY02
                CohortExportParityTests.Cohorts.OneSeriesWithTwoStructureSetsAndTwoDoses(input); // PARITY04
                string output = root.Folder("out");

                var (window, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, output);
                Assert.Equal(new[] { "PARITY01", "PARITY02", "PARITY04" }, vm.Patients.Select(p => p.Model.PatientID));
                var series04 = Assert.Single(Assert.Single(vm.Patients[2].Studies).ImageSeries);

                // The series with two structure sets offers Automatic and both; one structure set, no picker.
                Assert.True(series04.HasStructureSetChoice);
                Assert.Equal(new[] { null, "2.25.4120", "2.25.4130" },
                    series04.StructureSetChoices.Select(c => c.Model?.SeriesInstanceUID));
                Assert.Equal(SeriesGroupViewModel.AutomaticStructureSetLabel, series04.StructureSetChoices[0].Label);
                // Same description and ROI count: told apart by the tails of their UIDs.
                Assert.All(series04.StructureSetChoices.Skip(1), c => Assert.StartsWith("structures (2 ROIs) · …", c.Label));
                Assert.Equal(3, series04.StructureSetChoices.Select(c => c.Label).Distinct().Count());
                Assert.False(Assert.Single(Assert.Single(vm.Patients[0].Studies).ImageSeries).HasStructureSetChoice);
                Assert.Equal(new[] { "Target", "Avoid" }, series04.RoiNames);
                // Both structure sets' ROIs are discovered, and the second set's doses are listed too.
                Assert.Contains("Cord", vm.AllDiscoveredRoiNames);
                Assert.Equal("Plan A dose; Plan B dose", series04.LinkedRtDoseLabel);

                series04.SelectedStructureSet = series04.StructureSetChoices[2];
                Assert.Equal(new[] { "Body", "Cord" }, series04.RoiNames);

                vm.Patients[1].IsSelected = false;
                vm.RequireStructures = true;
                vm.SeriesDescriptionFilter = "  CT  ";
                vm.SpecifyOutputSpacing = true;
                vm.OutputSpacingX = 2.0;
                vm.OutputSpacingY = 2.0;
                vm.OutputSpacingZ = 1.5;
                vm.OnlyExportSpecificRois = true;
                await RoiSelectionDialog.SelectAsync(vm, new[] { "Target", "Body" });

                var options = vm.ExportOptionsFor(CohortMode.Convert);
                Assert.Equal(input, options.InputRoot);
                Assert.Equal(output, options.OutputRoot);
                Assert.Equal(new[] { "PARITY01", "PARITY04" }, options.PatientIds);
                Assert.Equal(new[] { "2.25.1110", "2.25.4110" }, options.SeriesInstanceUids);
                Assert.Equal(new[] { "2.25.4130" }, options.StructureSetUids);
                Assert.True(options.RequireStructures);
                Assert.False(options.RequireDose);
                Assert.Equal("CT", options.SeriesDescriptionFilter);
                Assert.Null(options.StructDescriptionFilter);
                Assert.Equal(new[] { 2.0, 2.0, 1.5 }, options.OutputSpacing);
                Assert.True(options.OnlyAssociatedRois);
                Assert.Equal(new[] { "Body", "Target" }, options.RoiSelection.OrderBy(n => n));
                Assert.False(options.Anonymize);
                Assert.Null(options.KeyPath);
                Assert.False(options.HasMetadataSelections);
                Assert.Equal(ExportManifestService.DefaultFileName, options.ManifestFileName);
                Assert.Equal(MainViewModel.SurveyManifestFileName, vm.ExportOptionsFor(CohortMode.Manifest).ManifestFileName);

                // The filter off: the selection is kept for next time but not passed.
                vm.OnlyExportSpecificRois = false;
                options = vm.ExportOptionsFor(CohortMode.Convert);
                Assert.False(options.OnlyAssociatedRois);
                Assert.Empty(options.RoiSelection);

                // A series unticked under a ticked patient, and the picker back on Automatic.
                series04.IsSelected = false;
                series04.SelectedStructureSet = series04.StructureSetChoices[0];
                options = vm.ExportOptionsFor(CohortMode.Convert);
                Assert.Equal(new[] { "2.25.1110" }, options.SeriesInstanceUids);
                Assert.Empty(options.StructureSetUids);

                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public async Task The_new_controls_are_on_screen()
        {
            using (new UserSettingsGuard())
            using (var root = new TempRoot())
            using (var launcher = new LauncherSession())
            {
                string input = root.Folder("dicom");
                CohortExportParityTests.Cohorts.OneSeriesWithTwoStructureSetsAndTwoDoses(input);
                var (window, vm) = await ForwardWindow.OpenScannedAsync(launcher, input, root.Folder("out"));

                // The series row's structure-set picker, with both doses listed under it.
                foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>().ToList())
                    item.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>().ToList())
                    item.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                var series = Assert.Single(Assert.Single(vm.Patients[0].Studies).ImageSeries);
                series.SelectedStructureSet = series.StructureSetChoices[2];
                Dispatcher.UIThread.RunJobs();
                var picker = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == series);
                Assert.True(picker.IsEffectivelyVisible, "the structure-set picker is hidden");
                Assert.Same(series.StructureSetChoices[2], picker.SelectedItem);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Plan A dose; Plan B dose");
                Screenshots.Capture(window, "17_dicom_to_nifti_structure_set_choice");

                // Export Options' series-selection section.
                vm.RequireDose = true;
                vm.StructDescriptionFilter = "clinical";
                var options = launcher.OpenedBy<ExportOptionsWindow>(() => vm.OpenExportOptionsCommand.Execute(null), "Export Options...");
                Dispatcher.UIThread.RunJobs();
                Assert.True(options.FindControl<CheckBox>("RequireStructuresCheckBox").IsEffectivelyVisible);
                Assert.True(options.FindControl<CheckBox>("RequireDoseCheckBox").IsChecked);
                Assert.Equal("clinical", options.FindControl<TextBox>("StructDescriptionFilterBox").Text);
                Assert.True(options.FindControl<TextBox>("SeriesDescriptionFilterBox").IsEffectivelyVisible);
                Screenshots.Capture(options, "16_dicom_to_nifti_export_options");
                Assert.Contains("series filter", vm.ExportOptionsSummary);
            }
        }
    }
}
