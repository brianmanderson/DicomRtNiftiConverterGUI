using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// ROI Association editor dialog. DataContext is a <see cref="RoiAssociationViewModel"/>. Save and
    /// Add/Remove are command-bound in XAML; Import/Export open Avalonia file pickers here (the VM stays
    /// UI-free) and hand the chosen path back to the VM. Close ends the dialog — the owner reloads the
    /// persisted associations afterwards.
    /// </summary>
    public partial class RoiAssociationWindow : Window
    {
        private static readonly FilePickerFileType JsonFiles =
            new("JSON files") { Patterns = new[] { "*.json" } };

        public RoiAssociationWindow() => InitializeComponent();

        private void DiscoveredRoiListBox_DoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is ListBox { SelectedItem: DiscoveredRoiName item } &&
                DataContext is RoiAssociationViewModel vm &&
                vm.AddDiscoveredNameAsAliasCommand.CanExecute(item.Name))
            {
                vm.AddDiscoveredNameAsAliasCommand.Execute(item.Name);
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not RoiAssociationViewModel vm) return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import ROI Associations",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType> { JsonFiles, FilePickerFileTypes.All },
            });

            string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path))
                vm.ImportFromFile(path);
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not RoiAssociationViewModel vm) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export ROI Associations",
                SuggestedFileName = "roi_associations.json",
                DefaultExtension = "json",
                FileTypeChoices = new List<FilePickerFileType> { JsonFiles },
            });

            string path = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                vm.ExportToFile(path);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close(true);
    }
}
