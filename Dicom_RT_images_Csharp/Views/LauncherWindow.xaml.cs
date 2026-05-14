using System;
using System.ComponentModel;
using System.Windows;
using Dicom_RT_images_Csharp.ViewModels;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Launcher window: top-level chooser between DICOM-&gt;NIfTI and NIfTI-&gt;DICOM workflows.
    /// Caches each workflow window+VM so that re-opening preserves session state.
    /// Workflow windows intercept their Closing event to Hide() instead of close,
    /// so the launcher reappears and the in-memory state survives.
    /// Closing the launcher itself (the app's MainWindow) triggers app shutdown.
    /// </summary>
    public partial class LauncherWindow : Window
    {
        private LauncherViewModel _vm;

        // Cached workflow windows (built lazily on first open).
        private MainWindow _dicomToNiftiWindow;
        private NiftiToDicomWindow _niftiToDicomWindow;

        public LauncherWindow()
        {
            InitializeComponent();
            DataContextChanged += LauncherWindow_DataContextChanged;
        }

        private void LauncherWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is LauncherViewModel oldVm)
            {
                oldVm.OpenDicomToNiftiRequested -= OnOpenDicomToNiftiRequested;
                oldVm.OpenNiftiToDicomRequested -= OnOpenNiftiToDicomRequested;
            }

            _vm = e.NewValue as LauncherViewModel;
            if (_vm != null)
            {
                _vm.OpenDicomToNiftiRequested += OnOpenDicomToNiftiRequested;
                _vm.OpenNiftiToDicomRequested += OnOpenNiftiToDicomRequested;
            }
        }

        private void OnOpenDicomToNiftiRequested(object sender, EventArgs e)
        {
            if (_dicomToNiftiWindow == null)
            {
                _dicomToNiftiWindow = new MainWindow();
                _dicomToNiftiWindow.DataContext = new MainViewModel(
                    _vm.ScannerService,
                    _vm.ConversionService,
                    _vm.MaskService,
                    _vm.SettingsService);

                // Hide instead of close so state persists.
                _dicomToNiftiWindow.Closing += WorkflowWindow_Closing;
            }

            ShowWorkflow(_dicomToNiftiWindow);
        }

        private void OnOpenNiftiToDicomRequested(object sender, EventArgs e)
        {
            if (_niftiToDicomWindow == null)
            {
                _niftiToDicomWindow = new NiftiToDicomWindow();
                _niftiToDicomWindow.DataContext = new NiftiToDicomViewModel(
                    _vm.ScannerService,
                    _vm.RtStructWriter,
                    _vm.RtDoseWriter,
                    _vm.NiftiMetadataService,
                    _vm.NiftiImageWriter);

                _niftiToDicomWindow.Closing += WorkflowWindow_Closing;
            }

            ShowWorkflow(_niftiToDicomWindow);
        }

        private void ShowWorkflow(Window workflow)
        {
            this.Hide();
            workflow.Show();
            if (workflow.WindowState == WindowState.Minimized)
                workflow.WindowState = WindowState.Normal;
            workflow.Activate();
        }

        /// <summary>
        /// Intercepts the X button on workflow windows: hide and reshow the launcher
        /// instead of really closing. Allows the underlying VM to retain in-memory state.
        /// During app shutdown the launcher is gone, so we let the close proceed.
        /// </summary>
        private void WorkflowWindow_Closing(object sender, CancelEventArgs e)
        {
            // If the launcher is no longer alive (shutdown in progress), let it close for real.
            if (!this.IsLoaded || Application.Current == null)
                return;

            if (sender is Window w)
            {
                e.Cancel = true;
                w.Hide();
                this.Show();
                this.Activate();
            }
        }

        /// <summary>
        /// When the launcher itself closes, dispose the cached workflow windows so the app exits cleanly.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // Detach hide-on-close interceptor so cached windows can really close.
            if (_dicomToNiftiWindow != null)
            {
                _dicomToNiftiWindow.Closing -= WorkflowWindow_Closing;
                _dicomToNiftiWindow.Close();
                _dicomToNiftiWindow = null;
            }
            if (_niftiToDicomWindow != null)
            {
                _niftiToDicomWindow.Closing -= WorkflowWindow_Closing;
                _niftiToDicomWindow.Close();
                _niftiToDicomWindow = null;
            }

            base.OnClosed(e);
        }
    }
}
