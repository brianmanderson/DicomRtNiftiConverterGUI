using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Read-only help window for the DICOM → NIfTI workflow. Opened non-modally from the
    /// forward window's Help button. No view-model — the content is static XAML.
    /// </summary>
    public partial class DicomToNiftiHelpWindow : Window
    {
        public DicomToNiftiHelpWindow() => InitializeComponent();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
