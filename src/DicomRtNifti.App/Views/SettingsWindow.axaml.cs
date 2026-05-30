using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Settings dialog. Folder selection uses Avalonia's IStorageProvider (the WPF version
    /// used System.Windows.Forms.FolderBrowserDialog). Shown via ShowDialog&lt;bool&gt;.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _settings;

        // Parameterless ctor for the Avalonia designer/XAML loader.
        public SettingsWindow() : this(new SettingsService(), new AppSettings()) { }

        public SettingsWindow(SettingsService settingsService, AppSettings settings)
        {
            InitializeComponent();
            _settingsService = settingsService;
            _settings = settings;

            OutputDirTextBox.Text = settings.DefaultOutputDirectory ?? "";
            AutoOpenCheckBox.IsChecked = settings.AutoOpenAfterConversion;
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            IStorageFolder start = null;
            if (!string.IsNullOrEmpty(OutputDirTextBox.Text) && Directory.Exists(OutputDirTextBox.Text))
            {
                start = await StorageProvider.TryGetFolderFromPathAsync(OutputDirTextBox.Text);
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select default output folder",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            if (folders.Count > 0)
            {
                string path = folders[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    OutputDirTextBox.Text = path;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.DefaultOutputDirectory = OutputDirTextBox.Text;
            _settings.AutoOpenAfterConversion = AutoOpenCheckBox.IsChecked == true;
            _settingsService.SaveSettings(_settings);
            Close(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
