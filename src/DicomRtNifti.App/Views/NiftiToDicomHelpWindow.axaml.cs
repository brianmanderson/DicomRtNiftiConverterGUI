using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Read-only help window for the NIfTI → DICOM workflow. Opened non-modally from the
    /// reverse window's Help button. No view-model — the content is static XAML.
    /// </summary>
    public partial class NiftiToDicomHelpWindow : Window
    {
        public NiftiToDicomHelpWindow() => InitializeComponent();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
