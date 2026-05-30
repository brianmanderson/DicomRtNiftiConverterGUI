using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// ROI-selection dialog. The DataContext is a RoiSelectionViewModel supplied by the
    /// caller; Confirm closes with true (read the VM's GetSelectedRoiNames()), Cancel false.
    /// </summary>
    public partial class RoiSelectionWindow : Window
    {
        public RoiSelectionWindow() => InitializeComponent();

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Close(true);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
