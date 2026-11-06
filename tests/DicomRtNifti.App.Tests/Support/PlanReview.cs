using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// Convert Selected and Export Manifest Only open their plan (<see cref="ExportPlanWindow"/>)
    /// modally and wait for the user. This drives that dialog as a user does: start the command,
    /// find the plan window it opened, let the test look at it, click Run or Cancel, and wait for
    /// the run to finish.
    /// </summary>
    public static class PlanReview
    {
        /// <summary>Runs <paramref name="command"/>, clicks the plan's Run button, and returns the plan it showed.</summary>
        public static Task<ExportPlanViewModel> RunAsync(Func<Task> command, Action<ExportPlanWindow> inspect = null)
            => ReviewAsync(command, run: true, inspect);

        /// <summary>Runs <paramref name="command"/>, clicks the plan's Cancel button, and returns the plan it showed.</summary>
        public static Task<ExportPlanViewModel> CancelAsync(Func<Task> command, Action<ExportPlanWindow> inspect = null)
            => ReviewAsync(command, run: false, inspect);

        /// <summary>
        /// Runs <paramref name="command"/> where it must stop before planning (a refused key
        /// location, say) and asserts that no plan opened. A plan that does open is cancelled, so
        /// the test fails instead of waiting on the dialog.
        /// </summary>
        public static async Task WithoutPlanAsync(Func<Task> command)
        {
            OpenWindows.Install();
            var before = OpenWindows.Current;
            Task running = command();
            Dispatcher.UIThread.RunJobs();
            var opened = OpenWindows.Current.Except(before).OfType<ExportPlanWindow>().ToList();
            foreach (var window in opened)
                Click(window, "CancelButton");
            Dispatcher.UIThread.RunJobs();
            await running;
            Dispatcher.UIThread.RunJobs();
            Assert.True(opened.Count == 0, "the command opened an export plan; it should have stopped before planning");
        }

        private static async Task<ExportPlanViewModel> ReviewAsync(Func<Task> command, bool run, Action<ExportPlanWindow> inspect)
        {
            OpenWindows.Install();
            var before = OpenWindows.Current;
            Task running = command();
            Dispatcher.UIThread.RunJobs();

            var opened = OpenWindows.Current.Except(before).OfType<ExportPlanWindow>().ToList();
            if (opened.Count != 1)
            {
                // The command ended without a plan (no output folder, nothing ticked): let it finish
                // so the failure says what it logged rather than hanging.
                if (running.IsCompleted) await running;
                Assert.Fail($"expected the command to open one export plan, found {opened.Count}");
            }

            var window = opened[0];
            var plan = Assert.IsType<ExportPlanViewModel>(window.DataContext);
            inspect?.Invoke(window);

            if (run)
                Assert.True(plan.CanRun, "the plan has nothing to run: " + plan.Headline);
            Click(window, run ? "RunButton" : "CancelButton");
            Dispatcher.UIThread.RunJobs();

            await running;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible, "the plan window is still open after its button was clicked");
            return plan;
        }

        /// <summary>Raises a click on the named button of <paramref name="window"/>, as a press of it does.</summary>
        public static void Click(Window window, string buttonName)
        {
            var button = window.FindControl<Button>(buttonName);
            Assert.True(button != null, $"no button named {buttonName} in {window.GetType().Name}");
            Assert.True(button.IsEffectivelyEnabled, $"{buttonName} is disabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
