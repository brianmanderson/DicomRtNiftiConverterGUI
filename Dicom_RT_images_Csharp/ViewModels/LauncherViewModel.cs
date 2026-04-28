using System;
using System.Windows.Input;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// Top-level launcher: lets the user choose between the two workflows
    /// (DICOM -> NIfTI and NIfTI -> DICOM). The View subscribes to these events
    /// and is responsible for actually opening / hiding the workflow windows.
    /// </summary>
    public class LauncherViewModel
    {
        public LauncherViewModel(
            DicomScannerService scannerService,
            NiftiConversionService conversionService,
            RtStructMaskService maskService,
            SettingsService settingsService,
            RtStructWriterService rtStructWriter,
            RtDoseWriterService rtDoseWriter)
        {
            ScannerService = scannerService;
            ConversionService = conversionService;
            MaskService = maskService;
            SettingsService = settingsService;
            RtStructWriter = rtStructWriter;
            RtDoseWriter = rtDoseWriter;

            OpenDicomToNiftiCommand = new RelayCommand(_ => OpenDicomToNiftiRequested?.Invoke(this, EventArgs.Empty));
            OpenNiftiToDicomCommand = new RelayCommand(_ => OpenNiftiToDicomRequested?.Invoke(this, EventArgs.Empty));
        }

        // Services exposed so the View can construct workflow VMs with the right dependencies.
        public DicomScannerService ScannerService { get; }
        public NiftiConversionService ConversionService { get; }
        public RtStructMaskService MaskService { get; }
        public SettingsService SettingsService { get; }
        public RtStructWriterService RtStructWriter { get; }
        public RtDoseWriterService RtDoseWriter { get; }

        public ICommand OpenDicomToNiftiCommand { get; }
        public ICommand OpenNiftiToDicomCommand { get; }

        /// <summary>Raised when the user clicks "DICOM → NIfTI".</summary>
        public event EventHandler OpenDicomToNiftiRequested;

        /// <summary>Raised when the user clicks "NIfTI → DICOM".</summary>
        public event EventHandler OpenNiftiToDicomRequested;
    }
}
