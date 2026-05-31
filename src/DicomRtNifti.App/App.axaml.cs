using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dicom_RT_images_Csharp.Services;
using Dicom_RT_images_Csharp.ViewModels;
using Dicom_RT_images_Csharp.Views;

namespace Dicom_RT_images_Csharp
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Composition root: build the Core services once (mirrors the legacy App.xaml.cs).
                var settingsService = new SettingsService();
                var scannerService = new DicomScannerService();
                var maskService = new RtStructMaskService();
                var conversionService = new NiftiConversionService(maskService);
                var rtStructWriter = new RtStructWriterService();
                var rtDoseWriter = new RtDoseWriterService();
                var niftiMetadata = new NiftiMetadataService();
                var niftiImageWriter = new NiftiImageWriterService(niftiMetadata);
                var folderPicker = new AvaloniaFolderPicker();

                var launcherVm = new LauncherViewModel(
                    scannerService, conversionService, maskService, settingsService,
                    rtStructWriter, rtDoseWriter, niftiMetadata, niftiImageWriter, folderPicker);

                desktop.MainWindow = new LauncherWindow { DataContext = launcherVm };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
