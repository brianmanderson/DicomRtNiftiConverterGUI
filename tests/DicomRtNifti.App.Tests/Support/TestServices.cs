using DicomRtNifti.App.ViewModels;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// The App's composition root (App.OnFrameworkInitializationCompleted) with the folder picker
    /// swapped for <see cref="FakeFolderPicker"/>: the same Core services, built once, handed to
    /// the same LauncherViewModel. Keep the constructor in step with App.axaml.cs.
    /// </summary>
    public sealed class TestServices
    {
        public TestServices()
        {
            Settings = new SettingsService();
            Scanner = new DicomScannerService();
            Mask = new RtStructMaskService();
            Conversion = new NiftiConversionService(Mask);
            RtStructWriter = new RtStructWriterService();
            RtDoseWriter = new RtDoseWriterService();
            NiftiMetadata = new NiftiMetadataService();
            NiftiImageWriter = new NiftiImageWriterService(NiftiMetadata);
            FolderPicker = new FakeFolderPicker();
        }

        public SettingsService Settings { get; }
        public DicomScannerService Scanner { get; }
        public RtStructMaskService Mask { get; }
        public NiftiConversionService Conversion { get; }
        public RtStructWriterService RtStructWriter { get; }
        public RtDoseWriterService RtDoseWriter { get; }
        public NiftiMetadataService NiftiMetadata { get; }
        public NiftiImageWriterService NiftiImageWriter { get; }
        public FakeFolderPicker FolderPicker { get; }

        public LauncherViewModel NewLauncherViewModel() =>
            new LauncherViewModel(Scanner, Conversion, Mask, Settings,
                RtStructWriter, RtDoseWriter, NiftiMetadata, NiftiImageWriter, FolderPicker);
    }
}
