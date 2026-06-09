using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// DICOM metadata-tag picker. The DataContext is a MetadataTagSelectionViewModel supplied by
    /// the caller; Confirm closes with true (read the VM's GetSelectedKeywords()), Cancel false.
    /// Mirrors RoiSelectionWindow.
    /// </summary>
    public partial class MetadataTagSelectionWindow : Window
    {
        public MetadataTagSelectionWindow() => InitializeComponent();

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Close(true);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
