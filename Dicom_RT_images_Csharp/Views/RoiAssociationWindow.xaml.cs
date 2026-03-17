using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for RoiAssociationWindow.xaml
    /// </summary>
    public partial class RoiAssociationWindow : Window
    {
        /// <summary>
        /// Initializes the ROI Association editor window.
        /// </summary>
        public RoiAssociationWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
