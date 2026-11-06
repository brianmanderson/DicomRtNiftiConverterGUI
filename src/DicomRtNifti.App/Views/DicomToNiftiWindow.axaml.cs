using System;
using Avalonia.Controls;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Forward workflow (DICOM -> NIfTI) window. DataContext is a MainViewModel supplied by
    /// the launcher. Opened non-modally via Show(). The view-model's dialogs (the export plan,
    /// Select ROIs, the editors) open over the active window, and over this one when no window is
    /// active, as in a headless session.
    /// </summary>
    public partial class DicomToNiftiWindow : Window
    {
        public DicomToNiftiWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, EventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.DialogOwner = this;
        }

        // Keep the read-only log scrolled to the latest line as it grows.
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.CaretIndex = tb.Text?.Length ?? 0;
        }
    }
}
