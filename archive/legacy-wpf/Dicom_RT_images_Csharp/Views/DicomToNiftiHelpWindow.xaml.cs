using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for DicomToNiftiHelpWindow.xaml
    /// </summary>
    public partial class DicomToNiftiHelpWindow : Window
    {
        public DicomToNiftiHelpWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
