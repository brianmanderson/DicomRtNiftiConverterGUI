using System;
using System.Reflection;
using Avalonia.Controls;
using DicomRtNifti.App.ViewModels;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Launcher window: the top-level chooser. It owns no services; the LauncherViewModel it is
    /// bound to carries the Core services built once in App.OnFrameworkInitializationCompleted.
    /// On each request event it constructs the workflow view-model from those services and opens
    /// the DICOM->NIfTI or NIfTI->DICOM window non-modally, so a user can keep several open.
    /// </summary>
    public partial class LauncherWindow : Window
    {
        private LauncherViewModel _vm;

        public LauncherWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Surface the build version and licence in the footer. The informational version is
            // "1.0.0+<commit sha>" (Directory.Build.props + Source Link), the same string the CLI
            // prints for --version; the sha is shortened to git's seven characters for the footer.
            VersionText.Text = $"DICOM RT Toolkit  ·  {GetDisplayVersion()}  ·  MIT licensed";
        }

        /// <summary>
        /// AssemblyInformationalVersion with its commit sha cut to seven characters, falling back
        /// to the assembly version when the attribute is absent.
        /// </summary>
        internal static string GetDisplayVersion()
        {
            var asm = typeof(LauncherWindow).Assembly;
            string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info))
                return asm.GetName().Version?.ToString(3) ?? "unknown";

            int plus = info.IndexOf('+');
            if (plus < 0 || info.Length - plus - 1 <= 7)
                return info;
            return info.Substring(0, plus + 1 + 7);
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
