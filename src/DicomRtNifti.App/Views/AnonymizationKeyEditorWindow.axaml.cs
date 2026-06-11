using Avalonia.Controls;
using Avalonia.Interactivity;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Anonymization-key editor dialog. The DataContext is an AnonymizationKeyEditorViewModel.
    /// Save validates and writes the key file then closes with true; Cancel closes with false.
    /// </summary>
    public partial class AnonymizationKeyEditorWindow : Window
    {
        public AnonymizationKeyEditorWindow() => InitializeComponent();

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is AnonymizationKeyEditorViewModel vm && vm.TrySave())
                Close(true);
            // On validation failure the VM sets ErrorText and the window stays open.
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
