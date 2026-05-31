using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Reverse workflow (NIfTI -> DICOM) window. DataContext is a NiftiToDicomViewModel
    /// supplied by the launcher. Opened non-modally via Show().
    /// </summary>
    public partial class NiftiToDicomWindow : Window
    {
        public NiftiToDicomWindow() => InitializeComponent();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
