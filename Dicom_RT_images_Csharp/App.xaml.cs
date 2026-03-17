using System.Windows;
using Dicom_RT_images_Csharp.Services;
using Dicom_RT_images_Csharp.ViewModels;

namespace Dicom_RT_images_Csharp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <inheritdoc/>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var settingsService = new SettingsService();
            var scannerService = new DicomScannerService();
            var maskService = new RtStructMaskService();
            var conversionService = new NiftiConversionService(maskService);

            var mainWindow = new MainWindow();
            mainWindow.DataContext = new MainViewModel(
                scannerService,
                conversionService,
                maskService,
                settingsService);
            mainWindow.Show();
        }
    }
}
