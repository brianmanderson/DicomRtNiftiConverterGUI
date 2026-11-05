using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// Drives Select ROIs for Export as a user does: open it from the forward window, tick exactly
    /// the given canonical names (every other one unticked), and confirm.
    /// </summary>
    public static class RoiSelectionDialog
    {
        public static async Task SelectAsync(MainViewModel vm, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            OpenWindows.Install();
            var before = OpenWindows.Current;
            Task running = vm.SelectRoisCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var window = Assert.Single(OpenWindows.Current.Except(before).OfType<RoiSelectionWindow>());
            var selection = Assert.IsType<RoiSelectionViewModel>(window.DataContext);
            var offered = selection.FilteredRoiItems.Select(i => i.CanonicalName).ToList();
            foreach (string name in wanted)
                Assert.True(offered.Contains(name, StringComparer.OrdinalIgnoreCase),
                    $"Select ROIs does not offer '{name}'; it offers [{string.Join(", ", offered)}]");
            foreach (var item in selection.FilteredRoiItems)
                item.IsSelected = wanted.Contains(item.CanonicalName);

            PlanReview.Click(window, "ConfirmButton");
            Dispatcher.UIThread.RunJobs();
            await running;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
