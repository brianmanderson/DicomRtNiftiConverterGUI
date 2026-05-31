using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// Top-level launcher: chooses between the two workflows. Holds the Core services (built
    /// once in the App composition root) so the View can construct workflow view-models with
    /// the right dependencies, mirroring the legacy LauncherViewModel.
    /// </summary>
    public partial class LauncherViewModel : ObservableObject
    {
        public LauncherViewModel(
            DicomScannerService scannerService,
            NiftiConversionService conversionService,
            RtStructMaskService maskService,
            SettingsService settingsService,
            RtStructWriterService rtStructWriter,
            RtDoseWriterService rtDoseWriter,
            NiftiMetadataService niftiMetadataService,
            NiftiImageWriterService niftiImageWriter,
            IFolderPicker folderPicker)
        {
            ScannerService = scannerService;
            ConversionService = conversionService;
            MaskService = maskService;
            SettingsService = settingsService;
            RtStructWriter = rtStructWriter;
            RtDoseWriter = rtDoseWriter;
            NiftiMetadataService = niftiMetadataService;
            NiftiImageWriter = niftiImageWriter;
            FolderPicker = folderPicker;
        }

        public DicomScannerService ScannerService { get; }
        public NiftiConversionService ConversionService { get; }
        public RtStructMaskService MaskService { get; }
        public SettingsService SettingsService { get; }
        public RtStructWriterService RtStructWriter { get; }
        public RtDoseWriterService RtDoseWriter { get; }
        public NiftiMetadataService NiftiMetadataService { get; }
        public NiftiImageWriterService NiftiImageWriter { get; }
        public IFolderPicker FolderPicker { get; }

        public event EventHandler OpenDicomToNiftiRequested;
        public event EventHandler OpenNiftiToDicomRequested;

        [RelayCommand]
        private void OpenDicomToNifti() => OpenDicomToNiftiRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void OpenNiftiToDicom() => OpenNiftiToDicomRequested?.Invoke(this, EventArgs.Empty);
    }
}
