using Avalonia.Controls;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Forward workflow (DICOM -> NIfTI) window. DataContext is a MainViewModel supplied by
    /// the launcher. Opened non-modally via Show().
    /// </summary>
    public partial class DicomToNiftiWindow : Window
    {
        public DicomToNiftiWindow() => InitializeComponent();

        // Keep the read-only log scrolled to the latest line as it grows.
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.CaretIndex = tb.Text?.Length ?? 0;
        }
    }
}
