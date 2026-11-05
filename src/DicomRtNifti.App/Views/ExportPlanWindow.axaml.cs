using Avalonia.Controls;
using Avalonia.Interactivity;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// The dry run shown before Convert Selected or Export Manifest Only starts
    /// (<see cref="ExportPlanViewModel"/>). Modal, owned by the forward window; closes with true
    /// when the user runs the plan and false when they cancel or close it.
    /// </summary>
    public partial class ExportPlanWindow : Window
    {
        public ExportPlanWindow() => InitializeComponent();

        private void RunButton_Click(object sender, RoutedEventArgs e) => Close(true);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);

        private async void CopyJsonButton_Click(object sender, RoutedEventArgs e)
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null && DataContext is ExportPlanViewModel plan)
                await clipboard.SetTextAsync(plan.Json);
        }
    }
}
