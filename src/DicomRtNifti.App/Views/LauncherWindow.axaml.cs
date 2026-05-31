using System;
using Avalonia.Controls;
using Dicom_RT_images_Csharp.ViewModels;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Launcher window: top-level chooser. Opens the NIfTI->DICOM workflow window (wired to a
    /// real view-model built from the launcher's services); the DICOM->NIfTI window is still a
    /// stub pending its Phase 4 port. The legacy cache + hide-on-close behaviour is re-added
    /// in a later increment.
    /// </summary>
    public partial class LauncherWindow : Window
    {
        private LauncherViewModel _vm;

        public LauncherWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, EventArgs e)
        {
            if (_vm != null)
            {
                _vm.OpenDicomToNiftiRequested -= OnOpenDicomToNifti;
                _vm.OpenNiftiToDicomRequested -= OnOpenNiftiToDicom;
            }

            _vm = DataContext as LauncherViewModel;
            if (_vm != null)
            {
                _vm.OpenDicomToNiftiRequested += OnOpenDicomToNifti;
                _vm.OpenNiftiToDicomRequested += OnOpenNiftiToDicom;
            }
        }

        private void OnOpenDicomToNifti(object sender, EventArgs e)
        {
            if (_vm == null) { new DicomToNiftiWindow().Show(); return; }

            var vm = new MainViewModel(
                _vm.ScannerService, _vm.ConversionService, _vm.MaskService, _vm.SettingsService, _vm.FolderPicker);
            new DicomToNiftiWindow { DataContext = vm }.Show();
        }

        private void OnOpenNiftiToDicom(object sender, EventArgs e)
        {
            if (_vm == null) { new NiftiToDicomWindow().Show(); return; }

            var vm = new NiftiToDicomViewModel(
                _vm.ScannerService, _vm.RtStructWriter, _vm.RtDoseWriter,
                _vm.NiftiMetadataService, _vm.NiftiImageWriter, _vm.FolderPicker);
            new NiftiToDicomWindow { DataContext = vm }.Show();
        }
    }
}
