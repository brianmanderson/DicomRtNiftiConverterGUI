using System.IO;
using System.Windows;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _settings;

        /// <summary>
        /// Initializes the Settings window with current settings.
        /// </summary>
        public SettingsWindow(SettingsService settingsService, AppSettings settings)
        {
            InitializeComponent();
            _settingsService = settingsService;
            _settings = settings;

            OutputDirTextBox.Text = settings.DefaultOutputDirectory ?? "";
            AutoOpenCheckBox.IsChecked = settings.AutoOpenAfterConversion;
            ExportUnmatchedCheckBox.IsChecked = settings.ExportUnmatchedRois;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select default output folder";
                if (!string.IsNullOrEmpty(OutputDirTextBox.Text) && Directory.Exists(OutputDirTextBox.Text))
                {
                    dialog.SelectedPath = OutputDirTextBox.Text;
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutputDirTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.DefaultOutputDirectory = OutputDirTextBox.Text;
            _settings.AutoOpenAfterConversion = AutoOpenCheckBox.IsChecked == true;
            _settings.ExportUnmatchedRois = ExportUnmatchedCheckBox.IsChecked == true;
            _settingsService.SaveSettings(_settings);
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
