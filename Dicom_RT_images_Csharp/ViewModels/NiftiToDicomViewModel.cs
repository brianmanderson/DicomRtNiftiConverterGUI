using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the "NIfTI to DICOM" window. Scans a DICOM folder, discovers .nii.gz
    /// masks under <DicomFolder>/masks/, and converts them to a single RT-STRUCT.
    /// </summary>
    public class NiftiToDicomViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly RtStructWriterService _rtStructWriter;

        private string _dicomFolder = "";
        private string _outputRtStructPath = "";
        private string _statusText = "Browse to a DICOM folder containing a 'masks/' subdirectory.";
        private bool _isBusy;
        private SeriesGroupViewModel _selectedImageSeries;
        private CancellationTokenSource _cts;

        public NiftiToDicomViewModel(DicomScannerService scannerService, RtStructWriterService rtStructWriter)
        {
            _scannerService = scannerService;
            _rtStructWriter = rtStructWriter;

            AvailableImageSeries = new ObservableCollection<SeriesGroupViewModel>();
            DiscoveredRoiNames = new ObservableCollection<string>();

            BrowseDicomFolderCommand = new RelayCommand(_ => BrowseDicomFolder(), _ => !IsBusy);
            BrowseOutputCommand = new RelayCommand(_ => BrowseOutput(), _ => !IsBusy);
            ConvertCommand = new RelayCommand(async _ => await ConvertAsync(),
                _ => !IsBusy
                     && !string.IsNullOrEmpty(DicomFolder)
                     && SelectedImageSeries != null
                     && DiscoveredRoiNames.Count > 0
                     && !string.IsNullOrEmpty(OutputRtStructPath));
        }

        public string DicomFolder
        {
            get { return _dicomFolder; }
            set { _dicomFolder = value; OnPropertyChanged(); }
        }

        public string OutputRtStructPath
        {
            get { return _outputRtStructPath; }
            set { _outputRtStructPath = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SeriesGroupViewModel> AvailableImageSeries { get; }
        public ObservableCollection<string> DiscoveredRoiNames { get; }

        public SeriesGroupViewModel SelectedImageSeries
        {
            get { return _selectedImageSeries; }
            set { _selectedImageSeries = value; OnPropertyChanged(); }
        }

        public ICommand BrowseDicomFolderCommand { get; }
        public ICommand BrowseOutputCommand { get; }
        public ICommand ConvertCommand { get; }

        // ------- private helpers -------

        private void BrowseDicomFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select the DICOM folder (must contain a 'masks/' subdirectory)";
                if (!string.IsNullOrEmpty(DicomFolder) && Directory.Exists(DicomFolder))
                    dialog.SelectedPath = DicomFolder;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                DicomFolder = dialog.SelectedPath;
            }

            // Suggest a default output path inside the chosen folder
            string defaultName = $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm";
            OutputRtStructPath = Path.Combine(DicomFolder, defaultName);

            RefreshDiscoveredMasks();
            _ = ScanDicomFolderAsync();
        }

        private void RefreshDiscoveredMasks()
        {
            DiscoveredRoiNames.Clear();
            if (string.IsNullOrEmpty(DicomFolder)) return;

            string masksDir = Path.Combine(DicomFolder, "masks");
            if (!Directory.Exists(masksDir))
            {
                StatusText = "No 'masks/' subdirectory found in the selected folder.";
                return;
            }

            var files = Directory.EnumerateFiles(masksDir, "*.nii.gz", SearchOption.TopDirectoryOnly)
                                 .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (files.Count == 0)
            {
                StatusText = "'masks/' subfolder is empty (no .nii.gz files found).";
                return;
            }

            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".nii.gz".Length);
                else
                    name = Path.GetFileNameWithoutExtension(name);
                DiscoveredRoiNames.Add(name);
            }

            StatusText = $"Found {DiscoveredRoiNames.Count} mask file(s).";
        }

        private async Task ScanDicomFolderAsync()
        {
            if (string.IsNullOrEmpty(DicomFolder)) return;

            IsBusy = true;
            AvailableImageSeries.Clear();
            SelectedImageSeries = null;
            StatusText = "Scanning DICOM folder...";

            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusText = msg);

            try
            {
                var patients = await Task.Run(() =>
                    _scannerService.ScanFolderAsync(DicomFolder, progress, _cts.Token)).ConfigureAwait(true);

                int seriesCount = 0;
                foreach (var patient in patients)
                {
                    foreach (var study in patient.Studies)
                    {
                        foreach (var series in study.Series)
                        {
                            if (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT")
                            {
                                AvailableImageSeries.Add(new SeriesGroupViewModel(series));
                                seriesCount++;
                            }
                        }
                    }
                }

                if (seriesCount == 0)
                {
                    StatusText = "No CT/MR/PT image series found in folder.";
                }
                else
                {
                    SelectedImageSeries = AvailableImageSeries[0];
                    string suffix = DiscoveredRoiNames.Count > 0
                        ? $", {DiscoveredRoiNames.Count} mask(s) ready"
                        : "";
                    StatusText = $"Scan complete: {seriesCount} image series found{suffix}.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Scan failed: {ex.Message}";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        private void BrowseOutput()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save RT-Structure Set As...",
                Filter = "DICOM RT-STRUCT (*.dcm)|*.dcm|All files (*.*)|*.*",
                DefaultExt = ".dcm",
                FileName = string.IsNullOrEmpty(OutputRtStructPath)
                    ? $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm"
                    : Path.GetFileName(OutputRtStructPath),
                InitialDirectory = string.IsNullOrEmpty(OutputRtStructPath)
                    ? DicomFolder
                    : Path.GetDirectoryName(OutputRtStructPath)
            };
            if (dialog.ShowDialog() == true)
            {
                OutputRtStructPath = dialog.FileName;
            }
        }

        private async Task ConvertAsync()
        {
            if (SelectedImageSeries == null || string.IsNullOrEmpty(OutputRtStructPath))
                return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusText = msg);

            string outPath = OutputRtStructPath;
            var refSeries = SelectedImageSeries.Model;
            string folder = DicomFolder;

            try
            {
                string written = await Task.Run(() =>
                    _rtStructWriter.ConvertMasksFolderToRtStruct(
                        folder, refSeries, outPath, progress, _cts.Token)).ConfigureAwait(true);

                StatusText = $"Done. Wrote {Path.GetFileName(written)}.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Conversion cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Conversion failed: {ex.Message}";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
