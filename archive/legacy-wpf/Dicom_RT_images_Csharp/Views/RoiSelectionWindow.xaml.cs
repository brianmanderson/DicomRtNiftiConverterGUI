using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for RoiSelectionWindow.xaml
    /// </summary>
    public partial class RoiSelectionWindow : Window
    {
        /// <summary>
        /// Initializes the ROI selection window.
        /// </summary>
        public RoiSelectionWindow()
        {
            InitializeComponent();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
