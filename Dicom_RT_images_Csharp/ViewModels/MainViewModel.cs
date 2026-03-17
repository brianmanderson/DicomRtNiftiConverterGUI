using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// Main ViewModel orchestrating scan and conversion workflows.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly NiftiConversionService _conversionService;
        private readonly RtStructMaskService _maskService;
        private readonly SettingsService _settingsService;

        private string _inputFolder = "";
        private string _outputFolder = "";
        private bool _isScanning;
        private bool _isConverting;
        private double _progressValue;
        private string _statusText = "Ready";
        private string _logText = "";
        private CancellationTokenSource _cts;
        private AppSettings _settings;
        private List<RoiAssociation> _associations;

        /// <summary>
        /// Creates a new MainViewModel with the required services.
        /// </summary>
        public MainViewModel(
            DicomScannerService scannerService,
            NiftiConversionService conversionService,
            RtStructMaskService maskService,
            SettingsService settingsService)
        {
            _scannerService = scannerService;
            _conversionService = conversionService;
            _maskService = maskService;
            _settingsService = settingsService;

            Patients = new ObservableCollection<PatientGroupViewModel>();

            BrowseInputCommand = new RelayCommand(_ => BrowseInput());
            BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
            ScanCommand = new RelayCommand(_ => ExecuteScan(), _ => !IsScanning && !IsConverting);
            ConvertSelectedCommand = new RelayCommand(_ => ExecuteConvert(), _ => !IsScanning && !IsConverting && Patients.Count > 0);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsScanning || IsConverting);
            ManageAssociationsCommand = new RelayCommand(_ => OpenAssociationsWindow());
            OpenSettingsCommand = new RelayCommand(_ => OpenSettingsWindow());

            // Load settings and associations
            _settings = _settingsService.LoadSettings();
            _associations = _settingsService.LoadAssociations();

            if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory))
            {
                _outputFolder = _settings.DefaultOutputDirectory;
            }
        }

        /// <summary>
        /// The input directory to scan for DICOM files.
        /// </summary>
        public string InputFolder
        {
            get { return _inputFolder; }
            set { _inputFolder = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The output directory for NIfTI files.
        /// </summary>
        public string OutputFolder
        {
            get { return _outputFolder; }
            set { _outputFolder = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether a scan is currently running.
        /// </summary>
        public bool IsScanning
        {
            get { return _isScanning; }
            set { _isScanning = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether a conversion is currently running.
        /// </summary>
        public bool IsConverting
        {
            get { return _isConverting; }
            set { _isConverting = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Progress bar value (0-100).
        /// </summary>
        public double ProgressValue
        {
            get { return _progressValue; }
            set { _progressValue = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Current status text.
        /// </summary>
        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Scrollable log output.
        /// </summary>
        public string LogText
        {
            get { return _logText; }
            set { _logText = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Patient groups discovered during scanning.
        /// </summary>
        public ObservableCollection<PatientGroupViewModel> Patients { get; }

        public ICommand BrowseInputCommand { get; }
        public ICommand BrowseOutputCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand ConvertSelectedCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ManageAssociationsCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        private void BrowseInput()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select DICOM input folder";
                if (!string.IsNullOrEmpty(InputFolder) && Directory.Exists(InputFolder))
                {
                    dialog.SelectedPath = InputFolder;
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    InputFolder = dialog.SelectedPath;
                }
            }
        }

        private void BrowseOutput()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select output folder for NIfTI files";
                if (!string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder))
                {
                    dialog.SelectedPath = OutputFolder;
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutputFolder = dialog.SelectedPath;
                }
            }
        }

        private async void ExecuteScan()
        {
            if (string.IsNullOrEmpty(InputFolder) || !Directory.Exists(InputFolder))
            {
                AppendLog("Error: Please select a valid input folder.");
                return;
            }

            IsScanning = true;
            _cts = new CancellationTokenSource();
            Patients.Clear();
            ProgressValue = 0;

            var progress = new Progress<string>(msg =>
            {
                StatusText = msg;
            });

            try
            {
                AppendLog($"Scanning {InputFolder}...");
                var results = await Task.Run(() =>
                    _scannerService.ScanFolderAsync(InputFolder, progress, _cts.Token)).ConfigureAwait(true);

                foreach (var patient in results)
                {
                    Patients.Add(new PatientGroupViewModel(patient));
                }

                AppendLog($"Found {results.Count} patient(s), {results.Sum(p => p.Studies.Count)} study(ies).");
                StatusText = "Scan complete.";
                ProgressValue = 100;
            }
            catch (OperationCanceledException)
            {
                AppendLog("Scan cancelled.");
                StatusText = "Cancelled.";
            }
            catch (Exception ex)
            {
                AppendLog($"Scan error: {ex.Message}");
                StatusText = "Scan failed.";
            }
            finally
            {
                IsScanning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async void ExecuteConvert()
        {
            if (string.IsNullOrEmpty(OutputFolder))
            {
                AppendLog("Error: Please select an output folder.");
                return;
            }

            if (!Directory.Exists(OutputFolder))
            {
                Directory.CreateDirectory(OutputFolder);
            }

            // Collect all checked series
            var selectedSeries = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.ImageSeries)
                    {
                        if (series.ExportImages)
                        {
                            selectedSeries.Add(series);
                        }
                    }
                }
            }

            if (selectedSeries.Count == 0)
            {
                AppendLog("No series selected for export.");
                return;
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();
            ProgressValue = 0;

            // Reload associations in case they were edited
            _associations = _settingsService.LoadAssociations();
            _settings = _settingsService.LoadSettings();

            IProgress<string> progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                AppendLog(msg);
            });

            try
            {
                int total = selectedSeries.Count;
                int completed = 0;

                foreach (var seriesVm in selectedSeries)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var model = seriesVm.Model;

                    // Build output path: OutputFolder/PatientID/SeriesDescription
                    string patientId = "Unknown";
                    foreach (var p in Patients)
                    {
                        foreach (var s in p.Studies)
                        {
                            if (s.ImageSeries.Contains(seriesVm))
                            {
                                patientId = p.Model.PatientID;
                                break;
                            }
                        }
                    }

                    string seriesLabel = string.IsNullOrEmpty(model.SeriesDescription)
                        ? model.SeriesInstanceUID.Substring(0, Math.Min(8, model.SeriesInstanceUID.Length))
                        : SanitizePath(model.SeriesDescription);
                    string dateLabel = string.IsNullOrEmpty(model.SeriesDate) ? "" : model.SeriesDate + "_";
                    string outputDir = Path.Combine(OutputFolder, SanitizePath(patientId), dateLabel + seriesLabel);
                    Directory.CreateDirectory(outputDir);

                    // Convert image series
                    progress.Report($"Converting images: {patientId}/{seriesLabel}");
                    await Task.Run(() =>
                        _conversionService.ConvertImageSeriesToNifti(model, outputDir, progress, _cts.Token)).ConfigureAwait(true);

                    // Convert RT Struct if requested
                    if (seriesVm.IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Rasterizing structures: {patientId}/{seriesLabel}");
                        await Task.Run(() =>
                            _conversionService.ConvertStructToNifti(
                                model.LinkedRtStruct, model, outputDir,
                                _associations, _settings.ExportUnmatchedRois,
                                progress, _cts.Token)).ConfigureAwait(true);
                    }

                    // Convert RT Dose if requested
                    if (seriesVm.IncludeDose && model.LinkedRtDose != null)
                    {
                        progress.Report($"Converting dose: {patientId}/{seriesLabel}");
                        await Task.Run(() =>
                            _conversionService.ConvertDoseToNifti(model.LinkedRtDose, outputDir, progress, _cts.Token)).ConfigureAwait(true);
                    }

                    completed++;
                    ProgressValue = (double)completed / total * 100;
                }

                AppendLog($"Conversion complete. {completed} series exported to {OutputFolder}");
                StatusText = "Conversion complete.";

                if (_settings.AutoOpenAfterConversion)
                {
                    Process.Start("explorer.exe", OutputFolder);
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Conversion cancelled.");
                StatusText = "Cancelled.";
            }
            catch (Exception ex)
            {
                AppendLog($"Conversion error: {ex.Message}");
                StatusText = "Conversion failed.";
            }
            finally
            {
                IsConverting = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
        }

        private void OpenAssociationsWindow()
        {
            var vm = new RoiAssociationViewModel(_settingsService);
            var window = new Views.RoiAssociationWindow();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
            // Reload associations after window closes
            _associations = _settingsService.LoadAssociations();
        }

        private void OpenSettingsWindow()
        {
            var window = new Views.SettingsWindow(_settingsService, _settings);
            window.Owner = Application.Current.MainWindow;
            if (window.ShowDialog() == true)
            {
                _settings = _settingsService.LoadSettings();
                if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory) && string.IsNullOrEmpty(OutputFolder))
                {
                    OutputFolder = _settings.DefaultOutputDirectory;
                }
            }
        }

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogText += $"[{timestamp}] {message}\n";
        }

        /// <summary>
        /// Removes invalid path characters from a string.
        /// </summary>
        private static string SanitizePath(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
