using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for NiftiToDicomWindow.xaml
    /// </summary>
    public partial class NiftiToDicomWindow : Window
    {
        public NiftiToDicomWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
