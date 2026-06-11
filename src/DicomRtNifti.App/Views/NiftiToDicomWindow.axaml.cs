using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Reverse workflow (NIfTI -> DICOM) window. DataContext is a NiftiToDicomViewModel
    /// supplied by the launcher. Opened non-modally via Show().
    /// </summary>
    public partial class NiftiToDicomWindow : Window
    {
        public NiftiToDicomWindow() => InitializeComponent();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            // Stop the server watcher timer (and cancel any in-flight conversion) so it
            // doesn't keep ticking after the window is gone.
            (DataContext as NiftiToDicomViewModel)?.OnWindowClosed();
            base.OnClosed(e);
        }
    }
}
