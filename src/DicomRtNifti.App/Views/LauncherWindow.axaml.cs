using System;
using Avalonia.Controls;
using Dicom_RT_images_Csharp.ViewModels;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Launcher window: top-level chooser between the DICOM->NIfTI and NIfTI->DICOM
    /// workflows. For now it simply opens the (stub) workflow windows; the legacy
    /// cache + hide-on-close state-preservation is re-added in Phase 4 once the real
    /// workflow windows exist.
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

        private void OnOpenDicomToNifti(object sender, EventArgs e) => new DicomToNiftiWindow().Show();

        private void OnOpenNiftiToDicom(object sender, EventArgs e) => new NiftiToDicomWindow().Show();
    }
}
