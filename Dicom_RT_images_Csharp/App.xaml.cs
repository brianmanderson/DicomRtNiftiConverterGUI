using System.Windows;
using Dicom_RT_images_Csharp.Services;
using Dicom_RT_images_Csharp.ViewModels;
using Dicom_RT_images_Csharp.Views;

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

            // Build all services once and share across both workflows.
            var settingsService   = new SettingsService();
            var scannerService    = new DicomScannerService();
            var maskService       = new RtStructMaskService();
            var conversionService = new NiftiConversionService(maskService);
            var rtStructWriter    = new RtStructWriterService();
            var rtDoseWriter      = new RtDoseWriterService();

            var launcherVm = new LauncherViewModel(
                scannerService, conversionService, maskService, settingsService, rtStructWriter, rtDoseWriter);

            var launcher = new LauncherWindow();
            launcher.DataContext = launcherVm;

            // Closing the launcher exits the app (default OnLastWindowClose may otherwise hold us
            // open on a hidden workflow window — explicitly tie shutdown to the launcher).
            this.MainWindow = launcher;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose;

            launcher.Show();
        }
    }
}
