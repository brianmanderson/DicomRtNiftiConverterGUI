using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dicom_RT_images_Csharp.ViewModels;

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

        private void DiscoveredRoiListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox != null && listBox.SelectedItem is string selectedName)
            {
                var vm = DataContext as RoiAssociationViewModel;
                if (vm != null && vm.AddDiscoveredNameAsAliasCommand.CanExecute(selectedName))
                {
                    vm.AddDiscoveredNameAsAliasCommand.Execute(selectedName);
                }
            }
        }
    }
}
