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

        // Global export options
        private bool _exportImages = true;
        private bool _includeStructures = true;
        private bool _includeDose = true;
        private bool _onlyExportSpecificRois = false;
        private bool _anonymizeExport = false;
        private bool _allPatientsSelected = true;

        // Tracks the user's ROI selection from the RoiSelectionWindow (null = not yet chosen)
        private HashSet<string> _selectedRoiNames;

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
            AllDiscoveredRoiNames = new ObservableCollection<string>();

            BrowseInputCommand = new RelayCommand(_ => BrowseInput());
            BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
            ScanCommand = new RelayCommand(_ => ExecuteScan(), _ => !IsScanning && !IsConverting);
            ConvertSelectedCommand = new RelayCommand(_ => ExecuteConvert(), _ => !IsScanning && !IsConverting && Patients.Count > 0);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsScanning || IsConverting);
            ManageAssociationsCommand = new RelayCommand(_ => OpenAssociationsWindow());
            SelectRoisCommand = new RelayCommand(_ => OpenRoiSelectionWindow());
            OpenAnonymizationKeyEditorCommand = new RelayCommand(_ => OpenAnonymizationKeyEditor());
            OpenSettingsCommand = new RelayCommand(_ => OpenSettingsWindow());
            SelectAllPatientsCommand = new RelayCommand(_ => ToggleSelectAllPatients());
            ExportMetaDataCommand = new RelayCommand(_ => ExecuteExportMetaData(), _ => !IsScanning && !IsConverting && Patients.Count > 0);

            // Load settings and associations
            _settings = _settingsService.LoadSettings();
            _associations = _settingsService.LoadAssociations();

            if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory))
            {
                _outputFolder = _settings.DefaultOutputDirectory;
            }

            // Initialize global options from settings
            _exportImages = _settings.ExportImages;
            _includeStructures = _settings.IncludeStructures;
            _includeDose = _settings.IncludeDose;
            _onlyExportSpecificRois = _settings.OnlyExportSpecificRois;
            _anonymizeExport = _settings.AnonymizeExport;
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
        /// Global toggle: whether to export image series as image.nii.gz.
        /// </summary>
        public bool ExportImages
        {
            get { return _exportImages; }
            set { _exportImages = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Global toggle: whether to include RT Struct masks in the export.
        /// </summary>
        public bool IncludeStructures
        {
            get { return _includeStructures; }
            set { _includeStructures = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Global toggle: whether to include RT Dose in the export.
        /// </summary>
        public bool IncludeDose
        {
            get { return _includeDose; }
            set { _includeDose = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// When true, only ROIs matching defined associations are exported.
        /// When false, all ROIs are exported.
        /// </summary>
        public bool OnlyExportSpecificRois
        {
            get { return _onlyExportSpecificRois; }
            set { _onlyExportSpecificRois = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// When true, exports use anonymized integer folder IDs and generate a CSV manifest.
        /// </summary>
        public bool AnonymizeExport
        {
            get { return _anonymizeExport; }
            set { _anonymizeExport = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Select/deselect all patients toggle. Setting this propagates to all patient ViewModels.
        /// </summary>
        public bool AllPatientsSelected
        {
            get { return _allPatientsSelected; }
            set
            {
                if (_allPatientsSelected != value)
                {
                    _allPatientsSelected = value;
                    OnPropertyChanged();

                    foreach (var patient in Patients)
                    {
                        patient.IsSelected = value;
                    }
                }
            }
        }

        /// <summary>
        /// Patient groups discovered during scanning.
        /// </summary>
        public ObservableCollection<PatientGroupViewModel> Patients { get; }

        /// <summary>
        /// All unique ROI names discovered across all scanned RTSTRUCT files, sorted alphabetically.
        /// </summary>
        public ObservableCollection<string> AllDiscoveredRoiNames { get; }

        public ICommand BrowseInputCommand { get; }
        public ICommand BrowseOutputCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand ConvertSelectedCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ManageAssociationsCommand { get; }
        public ICommand SelectRoisCommand { get; }
        public ICommand OpenAnonymizationKeyEditorCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand SelectAllPatientsCommand { get; }
        public ICommand ExportMetaDataCommand { get; }

        private void BrowseInput()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Select DICOM input folder";
            dialog.ValidateNames = false;
            dialog.CheckFileExists = false;
            dialog.CheckPathExists = true;
            dialog.FileName = "Select Folder";
            if (!string.IsNullOrEmpty(InputFolder) && Directory.Exists(InputFolder))
            {
                dialog.InitialDirectory = InputFolder;
            }
            if (dialog.ShowDialog() == true)
            {
                InputFolder = Path.GetDirectoryName(dialog.FileName);
            }
        }

        private void BrowseOutput()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Select output folder for NIfTI files";
            dialog.ValidateNames = false;
            dialog.CheckFileExists = false;
            dialog.CheckPathExists = true;
            dialog.FileName = "Select Folder";
            if (!string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder))
            {
                dialog.InitialDirectory = OutputFolder;
            }
            if (dialog.ShowDialog() == true)
            {
                OutputFolder = Path.GetDirectoryName(dialog.FileName);
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
            AllDiscoveredRoiNames.Clear();
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

                // Aggregate all discovered ROI names
                AggregateDiscoveredRoiNames();

                AppendLog($"Found {results.Count} patient(s), {results.Sum(p => p.Studies.Count)} study(ies), {AllDiscoveredRoiNames.Count} unique ROI name(s).");
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

        /// <summary>
        /// Collects all unique ROI names from scanned RTSTRUCT series across all patients.
        /// </summary>
        private void AggregateDiscoveredRoiNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in Patients)
            {
                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.ImageSeries)
                    {
                        foreach (var roiName in series.RoiNames)
                        {
                            names.Add(roiName);
                        }
                    }
                }
            }

            AllDiscoveredRoiNames.Clear();
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                AllDiscoveredRoiNames.Add(name);
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

            // Collect all selected series from selected patients
            var selectedSeries = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                if (!patient.IsSelected) continue;

                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.ImageSeries)
                    {
                        if (series.IsSelected)
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

            // Save global options to settings
            _settings.ExportImages = ExportImages;
            _settings.IncludeStructures = IncludeStructures;
            _settings.IncludeDose = IncludeDose;
            _settings.OnlyExportSpecificRois = OnlyExportSpecificRois;
            _settings.AnonymizeExport = AnonymizeExport;
            _settingsService.SaveSettings(_settings);

            // Reload associations in case they were edited
            _associations = _settingsService.LoadAssociations();

            // Always apply associations for renaming; only filter ROIs when checkbox is checked
            var effectiveAssociations = _associations;
            bool effectiveExportUnmatched = !OnlyExportSpecificRois;

            if (OnlyExportSpecificRois && _selectedRoiNames != null && _selectedRoiNames.Count > 0)
            {
                var filtered = new List<RoiAssociation>();
                var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var assoc in _associations)
                {
                    if (_selectedRoiNames.Contains(assoc.CanonicalName))
                    {
                        filtered.Add(assoc);
                        coveredNames.Add(assoc.CanonicalName);
                    }
                }

                foreach (var name in _selectedRoiNames)
                {
                    if (!coveredNames.Contains(name))
                    {
                        filtered.Add(new RoiAssociation
                        {
                            CanonicalName = name,
                            Aliases = new List<string> { name }
                        });
                    }
                }

                effectiveAssociations = filtered;
                effectiveExportUnmatched = false;
            }

            IProgress<string> progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                AppendLog(msg);
            });

            try
            {
                int total = selectedSeries.Count;
                int completed = 0;

                // Create anonymization service if anonymizing (loads existing key file for resume)
                AnonymizationService anonService = null;
                if (AnonymizeExport)
                {
                    string keyFilePath = Path.Combine(OutputFolder, "AnonymizationKey.json");
                    anonService = new AnonymizationService(keyFilePath, _settings.HashSalt);
                }

                // Track exported ROI names per series (keyed by seriesUID)
                var exportedRoisPerSeries = new Dictionary<string, List<string>>();
                var spacingPerSeries = new Dictionary<string, double[]>();
                var roiVolumesPerSeries = new Dictionary<string, Dictionary<string, double>>();

                foreach (var seriesVm in selectedSeries)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var model = seriesVm.Model;

                    // Find parent patient and study for this series
                    string patientId = "Unknown";
                    string studyUid = "";
                    string seriesUid = model.SeriesInstanceUID;

                    foreach (var p in Patients)
                    {
                        foreach (var s in p.Studies)
                        {
                            if (s.ImageSeries.Contains(seriesVm))
                            {
                                patientId = p.Model.PatientID;
                                studyUid = s.Model.StudyInstanceUID;
                                break;
                            }
                        }
                    }

                    // Build output path
                    string outputDir;
                    string displayLabel;

                    if (AnonymizeExport && anonService != null)
                    {
                        int exportId = anonService.GetOrAssignExportId(patientId, studyUid, seriesUid);
                        outputDir = Path.Combine(OutputFolder, exportId.ToString());
                        displayLabel = exportId.ToString();
                    }
                    else
                    {
                        string seriesLabel = string.IsNullOrEmpty(model.SeriesDescription)
                            ? model.SeriesInstanceUID.Substring(0, Math.Min(8, model.SeriesInstanceUID.Length))
                            : SanitizePath(model.SeriesDescription);
                        string dateLabel = string.IsNullOrEmpty(model.SeriesDate) ? "" : model.SeriesDate + "_";
                        outputDir = Path.Combine(OutputFolder, SanitizePath(patientId), dateLabel + seriesLabel);
                        displayLabel = patientId + "/" + seriesLabel;
                    }

                    Directory.CreateDirectory(outputDir);

                    // Convert image series (controlled by global ExportImages toggle)
                    double[] seriesSpacing = null;
                    if (ExportImages)
                    {
                        progress.Report($"Converting images: {displayLabel}");
                        seriesSpacing = await Task.Run(() =>
                            _conversionService.ConvertImageSeriesToNifti(model, outputDir, progress, _cts.Token)).ConfigureAwait(true);
                    }

                    // If images were not exported, still read spacing for the manifest
                    if (seriesSpacing == null)
                    {
                        seriesSpacing = await Task.Run(() =>
                            _conversionService.GetImageSpacing(model)).ConfigureAwait(true);
                    }

                    spacingPerSeries[seriesUid] = seriesSpacing;

                    // Track ROI names exported for this series
                    var exportedRoiNames = new List<string>();
                    Dictionary<string, double> roiVolumes = null;

                    // Convert RT Struct if global IncludeStructures is enabled
                    if (IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Rasterizing structures: {displayLabel}");

                        // Determine which ROI names will be exported for tracking
                        if (model.LinkedRtStruct.RoiNames != null)
                        {
                            if (effectiveAssociations != null && effectiveAssociations.Count > 0)
                            {
                                // Track matched association canonical names
                                foreach (var assoc in effectiveAssociations)
                                {
                                    foreach (var roiName in model.LinkedRtStruct.RoiNames)
                                    {
                                        if (assoc.Aliases.Any(d => string.Equals(d, roiName, StringComparison.OrdinalIgnoreCase))
                                            || string.Equals(assoc.CanonicalName, roiName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            exportedRoiNames.Add(assoc.CanonicalName);
                                            break;
                                        }
                                    }
                                }
                                // Also track unmatched ROIs when exporting all
                                if (effectiveExportUnmatched)
                                {
                                    var matchedDicomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var assoc in effectiveAssociations)
                                    {
                                        matchedDicomNames.Add(assoc.CanonicalName);
                                        foreach (var alias in assoc.Aliases)
                                            matchedDicomNames.Add(alias);
                                    }
                                    foreach (var roiName in model.LinkedRtStruct.RoiNames)
                                    {
                                        if (!matchedDicomNames.Contains(roiName))
                                            exportedRoiNames.Add(roiName);
                                    }
                                }
                            }
                            else
                            {
                                // No associations defined: export all ROIs with original names
                                exportedRoiNames.AddRange(model.LinkedRtStruct.RoiNames);
                            }
                        }

                        roiVolumes = await Task.Run(() =>
                            _conversionService.ConvertStructToNifti(
                                model.LinkedRtStruct, model, outputDir,
                                effectiveAssociations, effectiveExportUnmatched,
                                false,
                                progress, _cts.Token)).ConfigureAwait(true);
                    }

                    exportedRoisPerSeries[seriesUid] = exportedRoiNames;
                    if (roiVolumes != null)
                        roiVolumesPerSeries[seriesUid] = roiVolumes;

                    // Convert RT Dose if global IncludeDose is enabled
                    if (IncludeDose && model.LinkedRtDose != null)
                    {
                        progress.Report($"Converting dose: {displayLabel}");
                        await Task.Run(() =>
                            _conversionService.ConvertDoseToNifti(model.LinkedRtDose, outputDir, progress, _cts.Token)).ConfigureAwait(true);
                    }

                    completed++;
                    ProgressValue = (double)completed / total * 100;
                }

                // Save anonymization key file if anonymizing
                List<ManifestRow> manifestRows;
                if (AnonymizeExport && anonService != null)
                {
                    anonService.Save();
                    manifestRows = anonService.GetAllManifestRows();
                }
                else
                {
                    // Build manifest rows from tracked data when not anonymizing
                    manifestRows = new List<ManifestRow>();
                    foreach (var seriesVm in selectedSeries)
                    {
                        var m = seriesVm.Model;
                        string pid = "Unknown";
                        string suid = "";
                        foreach (var p in Patients)
                        {
                            foreach (var st in p.Studies)
                            {
                                if (st.ImageSeries.Contains(seriesVm))
                                {
                                    pid = p.Model.PatientID;
                                    suid = st.Model.StudyInstanceUID;
                                    break;
                                }
                            }
                        }
                        manifestRows.Add(new ManifestRow
                        {
                            MRN = pid,
                            StudyUID = suid,
                            SeriesUID = m.SeriesInstanceUID,
                            ExportID = -1
                        });
                    }
                }

                // Attach exported ROI names, spacing, and volumes to manifest rows
                foreach (var row in manifestRows)
                {
                    double[] spacing;
                    if (spacingPerSeries.TryGetValue(row.SeriesUID, out spacing))
                    {
                        row.SpacingX = spacing[0];
                        row.SpacingY = spacing[1];
                        row.SpacingZ = spacing[2];
                    }

                    Dictionary<string, double> volumes;
                    if (roiVolumesPerSeries.TryGetValue(row.SeriesUID, out volumes))
                    {
                        row.RoiVolumes = volumes;
                    }
                }

                // Collect all unique ROI names across exported series for CSV columns
                var allExportedRoiNames = new List<string>();
                if (IncludeStructures)
                {
                    var roiNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rois in exportedRoisPerSeries.Values)
                    {
                        foreach (var name in rois)
                        {
                            if (roiNameSet.Add(name))
                                allExportedRoiNames.Add(name);
                        }
                    }
                }

                WriteCsvManifest(manifestRows, OutputFolder, allExportedRoiNames);

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

        private async void ExecuteExportMetaData()
        {
            if (string.IsNullOrEmpty(OutputFolder))
            {
                StatusText = "Please select an output folder.";
                return;
            }

            Directory.CreateDirectory(OutputFolder);

            // Collect selected series
            var selectedSeries = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                if (!patient.IsSelected) continue;
                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.ImageSeries)
                    {
                        if (series.IsSelected) selectedSeries.Add(series);
                    }
                }
            }

            if (selectedSeries.Count == 0)
            {
                StatusText = "No series selected.";
                return;
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();

            // Reload associations
            _associations = _settingsService.LoadAssociations();
            var effectiveAssociations = _associations;
            bool effectiveExportUnmatched = !OnlyExportSpecificRois;

            if (OnlyExportSpecificRois && _selectedRoiNames != null && _selectedRoiNames.Count > 0)
            {
                var filtered = new List<RoiAssociation>();
                var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var assoc in _associations)
                {
                    if (_selectedRoiNames.Contains(assoc.CanonicalName))
                    {
                        filtered.Add(assoc);
                        coveredNames.Add(assoc.CanonicalName);
                    }
                }

                foreach (var name in _selectedRoiNames)
                {
                    if (!coveredNames.Contains(name))
                    {
                        filtered.Add(new RoiAssociation
                        {
                            CanonicalName = name,
                            Aliases = new List<string> { name }
                        });
                    }
                }

                effectiveAssociations = filtered;
                effectiveExportUnmatched = false;
            }

            IProgress<string> progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                AppendLog(msg);
            });

            try
            {
                int total = selectedSeries.Count;
                int completed = 0;

                var spacingPerSeries = new Dictionary<string, double[]>();
                var roiVolumesPerSeries = new Dictionary<string, Dictionary<string, double>>();

                foreach (var seriesVm in selectedSeries)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var model = seriesVm.Model;

                    string patientId = "Unknown";
                    string studyUid = "";
                    string seriesUid = model.SeriesInstanceUID;

                    foreach (var p in Patients)
                    {
                        foreach (var s in p.Studies)
                        {
                            if (s.ImageSeries.Contains(seriesVm))
                            {
                                patientId = p.Model.PatientID;
                                studyUid = s.Model.StudyInstanceUID;
                                break;
                            }
                        }
                    }

                    string displayLabel = $"{patientId}/{seriesUid.Substring(0, Math.Min(8, seriesUid.Length))}";

                    // Get spacing without writing images
                    progress.Report($"Reading spacing: {displayLabel}");
                    double[] seriesSpacing = await Task.Run(() =>
                        _conversionService.GetImageSpacing(model)).ConfigureAwait(true);
                    spacingPerSeries[seriesUid] = seriesSpacing;

                    // Compute ROI volumes without writing masks
                    if (IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Computing volumes: {displayLabel}");

                        var roiVolumes = await Task.Run(() =>
                            _conversionService.ComputeStructVolumes(
                                model.LinkedRtStruct, model,
                                effectiveAssociations, effectiveExportUnmatched,
                                progress, _cts.Token)).ConfigureAwait(true);

                        if (roiVolumes != null && roiVolumes.Count > 0)
                        {
                            roiVolumesPerSeries[seriesUid] = roiVolumes;
                            AppendLog($"  Found {roiVolumes.Count} ROI(s) for {displayLabel}: {string.Join(", ", roiVolumes.Keys)}");
                        }
                    }

                    completed++;
                    ProgressValue = (int)(100.0 * completed / total);
                }

                // Build manifest rows
                var manifestRows = new List<ManifestRow>();
                foreach (var seriesVm in selectedSeries)
                {
                    var m = seriesVm.Model;
                    string pid = "Unknown";
                    string suid = "";
                    foreach (var p in Patients)
                    {
                        foreach (var st in p.Studies)
                        {
                            if (st.ImageSeries.Contains(seriesVm))
                            {
                                pid = p.Model.PatientID;
                                suid = st.Model.StudyInstanceUID;
                                break;
                            }
                        }
                    }

                    var row = new ManifestRow
                    {
                        MRN = pid,
                        StudyUID = suid,
                        SeriesUID = m.SeriesInstanceUID,
                        ExportID = -1
                    };

                    double[] spacing;
                    if (spacingPerSeries.TryGetValue(row.SeriesUID, out spacing))
                    {
                        row.SpacingX = spacing[0];
                        row.SpacingY = spacing[1];
                        row.SpacingZ = spacing[2];
                    }

                    Dictionary<string, double> volumes;
                    if (roiVolumesPerSeries.TryGetValue(row.SeriesUID, out volumes))
                    {
                        row.RoiVolumes = volumes;
                    }

                    manifestRows.Add(row);
                }

                // Derive ROI column names directly from the computed volumes
                // This guarantees column names exactly match the volume dictionary keys
                var allExportedRoiNames = new List<string>();
                var roiNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var volDict in roiVolumesPerSeries.Values)
                {
                    foreach (var roiName in volDict.Keys)
                    {
                        if (roiNameSet.Add(roiName))
                            allExportedRoiNames.Add(roiName);
                    }
                }

                WriteCsvManifest(manifestRows, OutputFolder, allExportedRoiNames, "export_manifest_meta.csv");

                AppendLog($"Metadata export complete. {completed} series processed.");
                StatusText = "Metadata export complete.";
            }
            catch (OperationCanceledException)
            {
                AppendLog("Metadata export cancelled.");
                StatusText = "Cancelled.";
            }
            catch (Exception ex)
            {
                AppendLog($"Metadata export error: {ex.Message}");
                StatusText = "Metadata export failed.";
            }
            finally
            {
                IsConverting = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void ToggleSelectAllPatients()
        {
            AllPatientsSelected = !AllPatientsSelected;
        }

        private void Cancel()
        {
            _cts?.Cancel();
        }

        private void OpenAssociationsWindow()
        {
            var discoveredNames = AllDiscoveredRoiNames.ToList();
            var vm = new RoiAssociationViewModel(_settingsService, discoveredNames);
            var window = new Views.RoiAssociationWindow();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
            // Reload associations after window closes
            _associations = _settingsService.LoadAssociations();
        }

        private void OpenRoiSelectionWindow()
        {
            // Reload associations to pick up any recent edits
            _associations = _settingsService.LoadAssociations();

            var discoveredNames = AllDiscoveredRoiNames.ToList();

            // Build per-patient ROI name map for patient counts
            var perPatientRoiNames = new Dictionary<string, List<string>>();
            foreach (var patient in Patients)
            {
                var rois = new List<string>();
                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.ImageSeries)
                    {
                        if (series.RoiNames != null)
                        {
                            rois.AddRange(series.RoiNames);
                        }
                    }
                }
                if (rois.Count > 0)
                {
                    perPatientRoiNames[patient.Model.PatientID] = rois;
                }
            }

            var vm = new RoiSelectionViewModel(discoveredNames, _associations, _selectedRoiNames, perPatientRoiNames);
            var window = new Views.RoiSelectionWindow();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;

            if (window.ShowDialog() == true)
            {
                _selectedRoiNames = vm.GetSelectedRoiNames();

                int deselectedCount = 0;
                // De-select patients whose series have no ROIs matching the selection
                foreach (var patient in Patients)
                {
                    bool patientHasSelectedRoi = false;

                    foreach (var study in patient.Studies)
                    {
                        foreach (var series in study.ImageSeries)
                        {
                            if (series.RoiNames == null || series.RoiNames.Count == 0)
                                continue;

                            // Check if any of this series' ROI names resolve to a selected canonical name
                            foreach (var rawRoi in series.RoiNames)
                            {
                                string canonical = ResolveToCanonical(rawRoi, _associations);
                                if (_selectedRoiNames.Contains(canonical))
                                {
                                    patientHasSelectedRoi = true;
                                    break;
                                }
                            }

                            if (patientHasSelectedRoi) break;
                        }

                        if (patientHasSelectedRoi) break;
                    }

                    if (!patientHasSelectedRoi)
                    {
                        patient.IsSelected = false;
                        deselectedCount++;
                    }
                }

                AppendLog($"ROI selection confirmed: {_selectedRoiNames.Count} ROI(s) selected. {deselectedCount} patient(s) de-selected (missing all selected ROIs).");
            }
        }

        /// <summary>
        /// Resolves a raw DICOM ROI name to its canonical name using associations.
        /// If no association matches, the raw name is returned as-is.
        /// </summary>
        private string ResolveToCanonical(string rawName, List<RoiAssociation> associations)
        {
            if (associations == null || associations.Count == 0)
                return rawName;

            foreach (var assoc in associations)
            {
                if (string.Equals(assoc.CanonicalName, rawName, StringComparison.OrdinalIgnoreCase))
                    return assoc.CanonicalName;

                foreach (var alias in assoc.Aliases)
                {
                    if (string.Equals(alias, rawName, StringComparison.OrdinalIgnoreCase))
                        return assoc.CanonicalName;
                }
            }

            return rawName;
        }

        private void OpenAnonymizationKeyEditor()
        {
            string keyFilePath;
            if (!string.IsNullOrEmpty(OutputFolder))
            {
                keyFilePath = Path.Combine(OutputFolder, "AnonymizationKey.json");
            }
            else
            {
                keyFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DicomToNifti", "AnonymizationKey.json");
            }

            var window = new Views.AnonymizationKeyEditorWindow(keyFilePath);
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
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

        /// <summary>
        /// Writes the export manifest CSV to the output folder.
        /// </summary>
        private void WriteCsvManifest(List<ManifestRow> rows, string outputFolder, List<string> roiColumnNames = null, string fileName = "export_manifest.csv")
        {
            string csvPath = Path.Combine(outputFolder, fileName);
            using (var writer = new StreamWriter(csvPath))
            {
                // Build header
                var header = "MRN,StudyUID,SeriesUID,ExportID,SpacingX,SpacingY,SpacingZ";
                if (roiColumnNames != null && roiColumnNames.Count > 0)
                {
                    foreach (var roiName in roiColumnNames)
                    {
                        header += ",\"" + roiName.Replace("\"", "\"\"") + "\"";
                    }
                }
                writer.WriteLine(header);

                foreach (var row in rows)
                {
                    // Quote fields defensively in case any contain commas or semicolons
                    var line = string.Format("\"{0}\",\"{1}\",\"{2}\",{3},{4},{5},{6}",
                        row.MRN, row.StudyUID, row.SeriesUID, row.ExportID,
                        row.SpacingX, row.SpacingY, row.SpacingZ);

                    // Append ROI volume columns
                    if (roiColumnNames != null && roiColumnNames.Count > 0)
                    {
                        foreach (var roiName in roiColumnNames)
                        {
                            double volume;
                            if (row.RoiVolumes != null && row.RoiVolumes.TryGetValue(roiName, out volume))
                            {
                                line += "," + volume;
                            }
                            else
                            {
                                line += ",-1";
                            }
                        }
                    }

                    writer.WriteLine(line);
                }
            }
            AppendLog($"Wrote manifest: {csvPath}");
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
