using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for NiftiToDicomHelpWindow.xaml
    /// </summary>
    public partial class NiftiToDicomHelpWindow : Window
    {
        public NiftiToDicomHelpWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
