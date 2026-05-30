using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// Top-level launcher: lets the user choose between the two workflows. The View
    /// subscribes to these events and owns opening the workflow windows.
    ///
    /// (Phase 4 re-introduces the Core service dependencies and constructs the real
    /// workflow view-models, mirroring the legacy LauncherViewModel.)
    /// </summary>
    public partial class LauncherViewModel : ObservableObject
    {
        /// <summary>Raised when the user clicks "DICOM -> NIfTI".</summary>
        public event EventHandler OpenDicomToNiftiRequested;

        /// <summary>Raised when the user clicks "NIfTI -> DICOM".</summary>
        public event EventHandler OpenNiftiToDicomRequested;

        [RelayCommand]
        private void OpenDicomToNifti() => OpenDicomToNiftiRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void OpenNiftiToDicom() => OpenNiftiToDicomRequested?.Invoke(this, EventArgs.Empty);
    }
}
