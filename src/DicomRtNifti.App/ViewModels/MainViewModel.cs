using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.App.Services;
using DicomRtNifti.App.Views;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// Main ViewModel for the forward (DICOM -> NIfTI) workflow. Ported from WPF; the scan /
    /// convert / metadata logic is unchanged. Cross-platform changes: CommunityToolkit commands
    /// (RefreshCommands() raises CanExecuteChanged), IFolderPicker instead of WinForms dialogs,
    /// async ShowDialog for the OutputSpacing / RoiSelection / Settings / ROI-association /
    /// anonymization-key dialogs, and an OS-switched folder reveal. The Help and Export Options
    /// windows open non-modally via Show(); ROI associations load from disk and apply during export.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly NiftiConversionService _conversionService;
        private readonly RtStructMaskService _maskService;
        private readonly SettingsService _settingsService;
        private readonly IFolderPicker _folderPicker;

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

        // The non-modal Export Options companion window, tracked so a second click re-focuses the
        // open window instead of stacking duplicates. Null whenever the window is closed.
        private ExportOptionsWindow _exportOptionsWindow;

        private bool _exportImages = true;
        private bool _includeStructures = true;
        private bool _includeDose = true;
        private bool _onlyExportSpecificRois;
        private bool _anonymizeExport;
        private bool _allPatientsSelected = true;
        private bool _specifyOutputSpacing;
        private double _outputSpacingX = 1.0;
        private double _outputSpacingY = 1.0;
        private double _outputSpacingZ = 1.0;
        private bool _exportDicomMetadata;
        private List<string> _metadataImageTagKeywords = new List<string>();
        private List<string> _metadataStructureTagKeywords = new List<string>();
        private List<string> _metadataDoseTagKeywords = new List<string>();

        private HashSet<string> _selectedRoiNames;

        public MainViewModel(
            DicomScannerService scannerService,
            NiftiConversionService conversionService,
            RtStructMaskService maskService,
            SettingsService settingsService,
            IFolderPicker folderPicker)
        {
            _scannerService = scannerService;
            _conversionService = conversionService;
            _maskService = maskService;
            _settingsService = settingsService;
            _folderPicker = folderPicker;

            Patients = new ObservableCollection<PatientGroupViewModel>();
            Patients.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowTreeEmptyHint));
            AllDiscoveredRoiNames = new ObservableCollection<string>();

            BrowseInputCommand = new AsyncRelayCommand(BrowseInputAsync);
            BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputAsync);
            ScanCommand = new AsyncRelayCommand(ExecuteScanAsync,
                () => !IsScanning && !IsConverting && !string.IsNullOrWhiteSpace(InputFolder));
            ConvertSelectedCommand = new AsyncRelayCommand(ExecuteConvertAsync,
                () => !IsScanning && !IsConverting && Patients.Count > 0 && !string.IsNullOrWhiteSpace(OutputFolder));
            CancelCommand = new RelayCommand(Cancel, () => IsScanning || IsConverting);
            ManageAssociationsCommand = new AsyncRelayCommand(OpenAssociationsAsync);
            SelectRoisCommand = new AsyncRelayCommand(OpenRoiSelectionAsync);
            OpenAnonymizationKeyEditorCommand = new AsyncRelayCommand(OpenAnonymizationKeyEditorAsync);
            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
            SelectAllPatientsCommand = new RelayCommand(ToggleSelectAllPatients);
            ExportMetaDataCommand = new AsyncRelayCommand(ExecuteExportMetaDataAsync,
                () => !IsScanning && !IsConverting && Patients.Count > 0 && !string.IsNullOrWhiteSpace(OutputFolder));
            OpenOutputSpacingCommand = new AsyncRelayCommand(OpenOutputSpacingAsync);
            OpenExportOptionsCommand = new RelayCommand(OpenExportOptions);
            OpenMetadataTagsCommand = new AsyncRelayCommand(OpenMetadataTagsAsync);
            OpenHelpCommand = new RelayCommand(OpenHelp);

            _settings = _settingsService.LoadSettings();
            _associations = _settingsService.LoadAssociations();

            if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory))
                _outputFolder = _settings.DefaultOutputDirectory;

            _exportImages = _settings.ExportImages;
            _includeStructures = _settings.IncludeStructures;
            _includeDose = _settings.IncludeDose;
            _onlyExportSpecificRois = _settings.OnlyExportSpecificRois;
            _anonymizeExport = _settings.AnonymizeExport;
            _specifyOutputSpacing = _settings.SpecifyOutputSpacing;
            _outputSpacingX = _settings.OutputSpacingX;
            _outputSpacingY = _settings.OutputSpacingY;
            _outputSpacingZ = _settings.OutputSpacingZ;
            _exportDicomMetadata = _settings.ExportDicomMetadata;
            _metadataImageTagKeywords = _settings.MetadataImageTagKeywords ?? new List<string>();
            _metadataStructureTagKeywords = _settings.MetadataStructureTagKeywords ?? new List<string>();
            _metadataDoseTagKeywords = _settings.MetadataDoseTagKeywords ?? new List<string>();
        }

        public string InputFolder { get { return _inputFolder; } set { _inputFolder = value; OnPropertyChanged(); RefreshCommands(); } }
        public string OutputFolder { get { return _outputFolder; } set { _outputFolder = value; OnPropertyChanged(); RefreshCommands(); } }

        /// <summary>True when no scan results are loaded yet — drives the tree's empty-state hint.</summary>
        public bool ShowTreeEmptyHint => Patients.Count == 0;

        public bool IsScanning { get { return _isScanning; } set { _isScanning = value; OnPropertyChanged(); RefreshCommands(); } }
        public bool IsConverting { get { return _isConverting; } set { _isConverting = value; OnPropertyChanged(); RefreshCommands(); } }

        public double ProgressValue { get { return _progressValue; } set { _progressValue = value; OnPropertyChanged(); } }
        public string StatusText { get { return _statusText; } set { _statusText = value; OnPropertyChanged(); } }
        public string LogText { get { return _logText; } set { _logText = value; OnPropertyChanged(); } }

        public bool ExportImages { get { return _exportImages; } set { _exportImages = value; OnPropertyChanged(); } }
        public bool IncludeStructures { get { return _includeStructures; } set { _includeStructures = value; OnPropertyChanged(); } }
        public bool IncludeDose { get { return _includeDose; } set { _includeDose = value; OnPropertyChanged(); } }
        public bool OnlyExportSpecificRois { get { return _onlyExportSpecificRois; } set { _onlyExportSpecificRois = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public bool SpecifyOutputSpacing { get { return _specifyOutputSpacing; } set { _specifyOutputSpacing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public double OutputSpacingX { get { return _outputSpacingX; } set { _outputSpacingX = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public double OutputSpacingY { get { return _outputSpacingY; } set { _outputSpacingY = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public double OutputSpacingZ { get { return _outputSpacingZ; } set { _outputSpacingZ = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public bool AnonymizeExport { get { return _anonymizeExport; } set { _anonymizeExport = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public bool ExportDicomMetadata { get { return _exportDicomMetadata; } set { _exportDicomMetadata = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }

        /// <summary>
        /// One-line digest of the non-default Export Options, shown next to the Export Options
        /// button so hidden state (which lives in a separate window) is visible at a glance.
        /// </summary>
        public string ExportOptionsSummary
        {
            get
            {
                var parts = new List<string>();
                if (OnlyExportSpecificRois) parts.Add("ROI filter");
                if (SpecifyOutputSpacing)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.###}×{1:0.###}×{2:0.###} mm",
                        OutputSpacingX, OutputSpacingY, OutputSpacingZ));
                if (AnonymizeExport) parts.Add("anonymized");
                if (ExportDicomMetadata) parts.Add("tag sidecar");
                return parts.Count == 0 ? "Default options" : string.Join("  ·  ", parts);
            }
        }

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
                        patient.IsSelected = value;
                }
            }
        }

        public ObservableCollection<PatientGroupViewModel> Patients { get; }
        public ObservableCollection<string> AllDiscoveredRoiNames { get; }

        public IAsyncRelayCommand BrowseInputCommand { get; }
        public IAsyncRelayCommand BrowseOutputCommand { get; }
        public IAsyncRelayCommand ScanCommand { get; }
        public IAsyncRelayCommand ConvertSelectedCommand { get; }
        public IRelayCommand CancelCommand { get; }
        public IAsyncRelayCommand ManageAssociationsCommand { get; }
        public IAsyncRelayCommand SelectRoisCommand { get; }
        public IAsyncRelayCommand OpenAnonymizationKeyEditorCommand { get; }
        public IAsyncRelayCommand OpenSettingsCommand { get; }
        public IRelayCommand SelectAllPatientsCommand { get; }
        public IAsyncRelayCommand ExportMetaDataCommand { get; }
        public IAsyncRelayCommand OpenOutputSpacingCommand { get; }
        public IRelayCommand OpenExportOptionsCommand { get; }
        public IAsyncRelayCommand OpenMetadataTagsCommand { get; }
        public IRelayCommand OpenHelpCommand { get; }

        private void RefreshCommands()
        {
            ScanCommand.NotifyCanExecuteChanged();
            ConvertSelectedCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ExportMetaDataCommand.NotifyCanExecuteChanged();
        }

        private async Task BrowseInputAsync()
        {
            string picked = await _folderPicker.PickFolderAsync("Select DICOM input folder", InputFolder);
            if (!string.IsNullOrEmpty(picked)) InputFolder = picked;
        }

        private async Task BrowseOutputAsync()
        {
            string picked = await _folderPicker.PickFolderAsync("Select output folder for NIfTI files", OutputFolder);
            if (!string.IsNullOrEmpty(picked)) OutputFolder = picked;
        }

        private async Task ExecuteScanAsync()
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

            var progress = new Progress<string>(msg => StatusText = msg);

            try
            {
                AppendLog($"Scanning {InputFolder}...");
                var scanResult = await Task.Run(() =>
                    _scannerService.ScanFolderAsync(InputFolder, progress, _cts.Token)).ConfigureAwait(true);

                foreach (var patient in scanResult.Patients)
                    Patients.Add(new PatientGroupViewModel(patient));

                AggregateDiscoveredRoiNames();

                string summary = $"Found {scanResult.Patients.Count} patient(s), " +
                                 $"{scanResult.Patients.Sum(p => p.Studies.Count)} study(ies), " +
                                 $"{AllDiscoveredRoiNames.Count} unique ROI name(s).";
                if (scanResult.SkippedErrorCount > 0)
                    summary += $" Skipped {scanResult.SkippedErrorCount} file(s) due to errors.";
                AppendLog(summary);
                if (scanResult.SkippedErrorCount > 0)
                {
                    foreach (var sample in scanResult.SkippedErrorSamples)
                        AppendLog("  " + sample);
                    if (scanResult.SkippedErrorCount > scanResult.SkippedErrorSamples.Count)
                        AppendLog($"  ...and {scanResult.SkippedErrorCount - scanResult.SkippedErrorSamples.Count} more.");
                }
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
                RefreshCommands();
            }
        }

        private void AggregateDiscoveredRoiNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        foreach (var roiName in series.RoiNames)
                            names.Add(roiName);

            AllDiscoveredRoiNames.Clear();
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                AllDiscoveredRoiNames.Add(name);
        }

        /// <summary>
        /// Counts how many series each discovered ROI name appears in (case-insensitive, keeping the
        /// first-seen casing). Feeds the ROI Associations browser so it can show "Name (count)".
        /// </summary>
        private Dictionary<string, int> ComputeDiscoveredRoiCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        foreach (var roiName in series.RoiNames)
                            counts[roiName] = counts.TryGetValue(roiName, out int c) ? c + 1 : 1;
            return counts;
        }

        private async Task ExecuteConvertAsync()
        {
            if (string.IsNullOrEmpty(OutputFolder))
            {
                AppendLog("Error: Please select an output folder.");
                return;
            }
            if (!Directory.Exists(OutputFolder))
                Directory.CreateDirectory(OutputFolder);

            var selectedSeries = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                if (!patient.IsSelected) continue;
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        if (series.IsSelected)
                            selectedSeries.Add(series);
            }

            if (selectedSeries.Count == 0)
            {
                AppendLog("No series selected for export.");
                return;
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();
            ProgressValue = 0;

            _settings.ExportImages = ExportImages;
            _settings.IncludeStructures = IncludeStructures;
            _settings.IncludeDose = IncludeDose;
            _settings.OnlyExportSpecificRois = OnlyExportSpecificRois;
            _settings.AnonymizeExport = AnonymizeExport;
            _settings.SpecifyOutputSpacing = SpecifyOutputSpacing;
            _settings.OutputSpacingX = OutputSpacingX;
            _settings.OutputSpacingY = OutputSpacingY;
            _settings.OutputSpacingZ = OutputSpacingZ;
            _settings.ExportDicomMetadata = ExportDicomMetadata;
            _settings.MetadataImageTagKeywords = _metadataImageTagKeywords;
            _settings.MetadataStructureTagKeywords = _metadataStructureTagKeywords;
            _settings.MetadataDoseTagKeywords = _metadataDoseTagKeywords;
            _settingsService.SaveSettings(_settings);

            _associations = _settingsService.LoadAssociations();

            double[] targetSpacing = SpecifyOutputSpacing
                ? new[] { OutputSpacingX, OutputSpacingY, OutputSpacingZ }
                : null;

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
                        filtered.Add(new RoiAssociation { CanonicalName = name, Aliases = new List<string> { name } });
                }
                effectiveAssociations = filtered;
                effectiveExportUnmatched = false;
            }

            IProgress<string> progress = new Progress<string>(msg => { StatusText = msg; AppendLog(msg); });

            try
            {
                int total = selectedSeries.Count;
                int completed = 0;

                AnonymizationService anonService = null;
                if (AnonymizeExport)
                {
                    string keyFilePath = Path.Combine(OutputFolder, "AnonymizationKey.json");
                    anonService = new AnonymizationService(keyFilePath, _settings.HashSalt);
                }

                var spacingPerSeries = new Dictionary<string, double[]>();
                var roiVolumesPerSeries = new Dictionary<string, Dictionary<string, double>>();

                foreach (var seriesVm in selectedSeries)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var model = seriesVm.Model;

                    var (patientId, studyUid) = ResolveIds(seriesVm);
                    string seriesUid = model.SeriesInstanceUID;

                    string outputDir;
                    string displayLabel;
                    if (AnonymizeExport && anonService != null)
                    {
                        // Per-identifier hashes: a patient's datasets all nest under one patient-hash
                        // folder; each study/series gets its own hash. Hashes are already safe, but
                        // sanitize defensively so every exported segment is valid on Windows.
                        string pHash = WindowsPathSanitizer.SanitizeName(anonService.GetPatientHash(patientId));
                        string stHash = WindowsPathSanitizer.SanitizeName(anonService.GetStudyHash(studyUid));
                        string seHash = WindowsPathSanitizer.SanitizeName(anonService.GetSeriesHash(seriesUid));
                        outputDir = Path.Combine(OutputFolder, pHash, stHash, seHash);
                        displayLabel = $"{pHash}/{stHash}/{seHash}";
                    }
                    else
                    {
                        string seriesLabel = string.IsNullOrEmpty(model.SeriesDescription)
                            ? model.SeriesInstanceUID.Substring(0, Math.Min(8, model.SeriesInstanceUID.Length))
                            : model.SeriesDescription;
                        string dateLabel = string.IsNullOrEmpty(model.SeriesDate) ? "" : model.SeriesDate + "_";
                        outputDir = Path.Combine(OutputFolder,
                            WindowsPathSanitizer.SanitizeName(patientId),
                            WindowsPathSanitizer.SanitizeName(dateLabel + seriesLabel));
                        displayLabel = patientId + "/" + seriesLabel;
                    }

                    Directory.CreateDirectory(outputDir);

                    double[] seriesSpacing = null;
                    if (ExportImages)
                    {
                        progress.Report($"Converting images: {displayLabel}");
                        seriesSpacing = await Task.Run(() =>
                            _conversionService.ConvertImageSeriesToNifti(model, outputDir, progress, _cts.Token, targetSpacing)).ConfigureAwait(true);
                    }

                    if (seriesSpacing == null)
                        seriesSpacing = await Task.Run(() => _conversionService.GetImageSpacing(model)).ConfigureAwait(true);

                    if (targetSpacing != null)
                        seriesSpacing = (double[])targetSpacing.Clone();

                    spacingPerSeries[seriesUid] = seriesSpacing;

                    Dictionary<string, double> roiVolumes = null;

                    if (IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Rasterizing structures: {displayLabel}");

                        roiVolumes = await Task.Run(() =>
                            _conversionService.ConvertStructToNifti(
                                model.LinkedRtStruct, model, outputDir,
                                effectiveAssociations, effectiveExportUnmatched,
                                false, progress, _cts.Token, targetSpacing)).ConfigureAwait(true);
                    }

                    // The conversion returns volumes keyed by the exported (canonical-or-raw) mask name,
                    // so the manifest columns come straight from what was actually written — keeping the
                    // CSV in lock-step with the masks and with the forgiving association matching.
                    if (roiVolumes != null)
                        roiVolumesPerSeries[seriesUid] = roiVolumes;

                    if (IncludeDose && model.LinkedRtDose != null)
                    {
                        progress.Report($"Converting dose: {displayLabel}");
                        await Task.Run(() =>
                            _conversionService.ConvertDoseToNifti(model.LinkedRtDose, outputDir, progress, _cts.Token, targetSpacing)).ConfigureAwait(true);
                    }

                    // Sidecar metadata.json: a section per modality (image tags from the series' first
                    // slice, structure tags from the linked RTSTRUCT, dose tags from the linked RTDOSE).
                    // Independent of the per-modality export toggles (outputDir always exists) and of
                    // ExportImages. Tags are written verbatim, so warn when this lands in an anonymized export.
                    if (ExportDicomMetadata &&
                        (_metadataImageTagKeywords.Count > 0 || _metadataStructureTagKeywords.Count > 0 ||
                         _metadataDoseTagKeywords.Count > 0))
                    {
                        if (AnonymizeExport)
                            AppendLog($"  Warning: metadata.json for {displayLabel} writes selected tags verbatim — may include PHI in an anonymized export.");
                        try
                        {
                            var request = new MetadataExportRequest
                            {
                                ImageFilePaths = model.FilePaths,
                                StructureFilePath = (model.LinkedRtStruct != null && model.LinkedRtStruct.FilePaths.Count > 0)
                                    ? model.LinkedRtStruct.FilePaths[0] : null,
                                DoseFilePath = (model.LinkedRtDose != null && model.LinkedRtDose.FilePaths.Count > 0)
                                    ? model.LinkedRtDose.FilePaths[0] : null,
                                ImageKeywords = _metadataImageTagKeywords,
                                StructureKeywords = _metadataStructureTagKeywords,
                                DoseKeywords = _metadataDoseTagKeywords,
                                ImageVoxelSpacing = seriesSpacing
                            };
                            string metaPath = Path.Combine(outputDir, "metadata.json");
                            await Task.Run(() =>
                                DicomMetadataExtractor.WriteMetadataJson(request, metaPath), _cts.Token).ConfigureAwait(true);
                            progress.Report($"Wrote metadata.json: {displayLabel}");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"  metadata.json failed for {displayLabel}: {ex.Message}");
                        }
                    }

                    completed++;
                    ProgressValue = (double)completed / total * 100;
                }

                var manifestRows = BuildManifestRows(
                    selectedSeries,
                    AnonymizeExport ? anonService : null,
                    spacingPerSeries,
                    roiVolumesPerSeries);

                if (AnonymizeExport && anonService != null)
                    anonService.Save();

                var allExportedRoiNames = new List<string>();
                if (IncludeStructures)
                {
                    var roiNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var volDict in roiVolumesPerSeries.Values)
                        foreach (var roiName in volDict.Keys)
                            if (roiNameSet.Add(roiName))
                                allExportedRoiNames.Add(roiName);
                }

                WriteCsvManifest(manifestRows, OutputFolder, allExportedRoiNames);

                AppendLog($"Conversion complete. {completed} series exported to {OutputFolder}");
                StatusText = "Conversion complete.";

                if (_settings.AutoOpenAfterConversion)
                    RevealFolder(OutputFolder);
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
                RefreshCommands();
            }
        }

        private async Task ExecuteExportMetaDataAsync()
        {
            if (string.IsNullOrEmpty(OutputFolder))
            {
                StatusText = "Please select an output folder.";
                return;
            }
            Directory.CreateDirectory(OutputFolder);

            var selectedSeries = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                if (!patient.IsSelected) continue;
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        if (series.IsSelected) selectedSeries.Add(series);
            }

            if (selectedSeries.Count == 0)
            {
                StatusText = "No series selected.";
                return;
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();
            _associations = _settingsService.LoadAssociations();

            double[] targetSpacing = SpecifyOutputSpacing
                ? new[] { OutputSpacingX, OutputSpacingY, OutputSpacingZ }
                : null;

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
                    if (!coveredNames.Contains(name))
                        filtered.Add(new RoiAssociation { CanonicalName = name, Aliases = new List<string> { name } });
                effectiveAssociations = filtered;
                effectiveExportUnmatched = false;
            }

            IProgress<string> progress = new Progress<string>(msg => { StatusText = msg; AppendLog(msg); });

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
                    var (patientId, _) = ResolveIds(seriesVm);
                    string seriesUid = model.SeriesInstanceUID;

                    string displayLabel = $"{patientId}/{seriesUid.Substring(0, Math.Min(8, seriesUid.Length))}";

                    progress.Report($"Reading spacing: {displayLabel}");
                    double[] seriesSpacing = await Task.Run(() => _conversionService.GetImageSpacing(model)).ConfigureAwait(true);
                    if (targetSpacing != null)
                        seriesSpacing = (double[])targetSpacing.Clone();
                    spacingPerSeries[seriesUid] = seriesSpacing;

                    if (IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Computing volumes: {displayLabel}");
                        var roiVolumes = await Task.Run(() =>
                            _conversionService.ComputeStructVolumes(
                                model.LinkedRtStruct, model,
                                effectiveAssociations, effectiveExportUnmatched,
                                progress, _cts.Token, targetSpacing)).ConfigureAwait(true);

                        if (roiVolumes != null && roiVolumes.Count > 0)
                        {
                            roiVolumesPerSeries[seriesUid] = roiVolumes;
                            AppendLog($"  Found {roiVolumes.Count} ROI(s) for {displayLabel}: {string.Join(", ", roiVolumes.Keys)}");
                        }
                    }

                    completed++;
                    ProgressValue = (int)(100.0 * completed / total);
                }

                // Honor the Anonymize toggle for metadata export too: when on, the manifest's
                // PatientID/StudyUID/SeriesUID columns carry hashes and the key file is written/extended.
                AnonymizationService anonService = null;
                if (AnonymizeExport)
                {
                    string keyFilePath = Path.Combine(OutputFolder, "AnonymizationKey.json");
                    anonService = new AnonymizationService(keyFilePath, _settings.HashSalt);
                }

                var manifestRows = BuildManifestRows(selectedSeries, anonService, spacingPerSeries, roiVolumesPerSeries);

                if (anonService != null)
                    anonService.Save();

                var allExportedRoiNames = new List<string>();
                var roiNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var volDict in roiVolumesPerSeries.Values)
                    foreach (var roiName in volDict.Keys)
                        if (roiNameSet.Add(roiName))
                            allExportedRoiNames.Add(roiName);

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
                RefreshCommands();
            }
        }

        private void ToggleSelectAllPatients() => AllPatientsSelected = !AllPatientsSelected;

        private void Cancel() => _cts?.Cancel();

        private async Task OpenOutputSpacingAsync()
        {
            var window = new OutputSpacingWindow(OutputSpacingX, OutputSpacingY, OutputSpacingZ);
            bool ok = await window.ShowDialog<bool>(AppWindows.Active);
            if (ok)
            {
                OutputSpacingX = window.SpacingX;
                OutputSpacingY = window.SpacingY;
                OutputSpacingZ = window.SpacingZ;
                _settings.OutputSpacingX = OutputSpacingX;
                _settings.OutputSpacingY = OutputSpacingY;
                _settings.OutputSpacingZ = OutputSpacingZ;
                _settingsService.SaveSettings(_settings);
                AppendLog($"Output spacing set to {OutputSpacingX} x {OutputSpacingY} x {OutputSpacingZ} mm");
            }
        }

        private async Task OpenRoiSelectionAsync()
        {
            _associations = _settingsService.LoadAssociations();
            var discoveredNames = AllDiscoveredRoiNames.ToList();

            var perPatientRoiNames = new Dictionary<string, List<string>>();
            foreach (var patient in Patients)
            {
                var rois = new List<string>();
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        if (series.RoiNames != null)
                            rois.AddRange(series.RoiNames);
                if (rois.Count > 0)
                    perPatientRoiNames[patient.Model.PatientID] = rois;
            }

            var vm = new RoiSelectionViewModel(discoveredNames, _associations, _selectedRoiNames, perPatientRoiNames);
            var window = new RoiSelectionWindow { DataContext = vm };

            if (await window.ShowDialog<bool>(AppWindows.Active))
            {
                _selectedRoiNames = vm.GetSelectedRoiNames();
                int deselectedCount = 0;
                foreach (var patient in Patients)
                {
                    bool patientHasSelectedRoi = false;
                    foreach (var study in patient.Studies)
                    {
                        foreach (var series in study.ImageSeries)
                        {
                            if (series.RoiNames == null || series.RoiNames.Count == 0) continue;
                            foreach (var rawRoi in series.RoiNames)
                            {
                                if (_selectedRoiNames.Contains(ResolveToCanonical(rawRoi, _associations)))
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

        private string ResolveToCanonical(string rawName, List<RoiAssociation> associations)
            => RoiNameMatcher.ResolveToCanonical(rawName, associations);

        private async Task OpenSettingsAsync()
        {
            var window = new SettingsWindow(_settingsService, _settings);
            if (await window.ShowDialog<bool>(AppWindows.Active))
            {
                _settings = _settingsService.LoadSettings();
                if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory) && string.IsNullOrEmpty(OutputFolder))
                    OutputFolder = _settings.DefaultOutputDirectory;
            }
        }

        private async Task OpenAssociationsAsync()
        {
            // Seed the editor with every ROI name discovered in the current scan (with how many
            // series each was found in) so the user can double-click discovered names into an alias set.
            var vm = new RoiAssociationViewModel(_settingsService, ComputeDiscoveredRoiCounts());
            var window = new RoiAssociationWindow { DataContext = vm };
            await window.ShowDialog<bool>(AppWindows.Active);

            // The editor persists on Save; reload so a subsequent export/ROI-selection picks up edits.
            _associations = _settingsService.LoadAssociations();
            AppendLog($"ROI Associations editor closed. {_associations.Count} association(s) loaded.");
        }

        private async Task OpenAnonymizationKeyEditorAsync()
        {
            // Edit the key file that an anonymized export to the current output folder would use;
            // fall back to the %AppData% location (inspection only) when no output folder is set.
            string keyFilePath = !string.IsNullOrEmpty(OutputFolder)
                ? Path.Combine(OutputFolder, "AnonymizationKey.json")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DicomToNifti", "AnonymizationKey.json");

            var vm = new AnonymizationKeyEditorViewModel(keyFilePath, _settings.HashSalt);
            var window = new AnonymizationKeyEditorWindow { DataContext = vm };
            if (await window.ShowDialog<bool>(AppWindows.Active))
                AppendLog($"Anonymization key saved: {keyFilePath}");
        }

        /// <summary>
        /// Opens the Export Options as a non-modal companion window bound to this same view-model,
        /// so its toggles drive the live export state while the main window stays usable. A second
        /// invocation re-focuses the already-open window rather than opening another.
        /// </summary>
        private void OpenExportOptions()
        {
            if (_exportOptionsWindow != null)
            {
                _exportOptionsWindow.Activate();
                return;
            }

            _exportOptionsWindow = new ExportOptionsWindow { DataContext = this };
            _exportOptionsWindow.Closed += (_, _) => _exportOptionsWindow = null;

            var owner = AppWindows.Active;
            if (owner != null)
                _exportOptionsWindow.Show(owner);
            else
                _exportOptionsWindow.Show();
        }

        /// <summary>
        /// Opens the metadata-tag picker modally, seeded with the currently-selected keywords. On
        /// confirm, stores the new selection and persists it (with the toggle) to settings so the
        /// choice survives across sessions and is ready for the next export.
        /// </summary>
        private async Task OpenMetadataTagsAsync()
        {
            var vm = new MetadataTagSelectionViewModel(
                _metadataImageTagKeywords, _metadataStructureTagKeywords, _metadataDoseTagKeywords);
            var window = new MetadataTagSelectionWindow { DataContext = vm };
            if (await window.ShowDialog<bool>(AppWindows.Active))
            {
                _metadataImageTagKeywords = vm.ImagesSection.GetSelectedKeywords();
                _metadataStructureTagKeywords = vm.StructuresSection.GetSelectedKeywords();
                _metadataDoseTagKeywords = vm.DoseSection.GetSelectedKeywords();
                _settings.ExportDicomMetadata = ExportDicomMetadata;
                _settings.MetadataImageTagKeywords = _metadataImageTagKeywords;
                _settings.MetadataStructureTagKeywords = _metadataStructureTagKeywords;
                _settings.MetadataDoseTagKeywords = _metadataDoseTagKeywords;
                _settingsService.SaveSettings(_settings);
                AppendLog($"Metadata tags selected: {_metadataImageTagKeywords.Count} image, " +
                          $"{_metadataStructureTagKeywords.Count} structure, {_metadataDoseTagKeywords.Count} dose.");
            }
        }

        private void OpenHelp()
        {
            var help = new DicomToNiftiHelpWindow();
            var owner = AppWindows.Active;
            if (owner != null)
                help.Show(owner);
            else
                help.Show();
        }

        private static void RevealFolder(string folder)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo("open", folder) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", folder) { UseShellExecute = true });
            }
            catch { /* best-effort convenience */ }
        }

        /// <summary>
        /// Writes the export manifest CSV via <see cref="ExportManifestService"/>, which merges
        /// into an existing file rather than overwriting it so a cohort can grow across runs.
        /// </summary>
        private void WriteCsvManifest(List<ManifestRow> rows, string outputFolder, List<string> roiColumnNames = null, string fileName = ExportManifestService.DefaultFileName)
        {
            string csvPath = ExportManifestService.Write(
                Path.Combine(outputFolder, fileName), rows, roiColumnNames);
            AppendLog($"Wrote manifest: {csvPath}");
        }

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogText += $"[{timestamp}] {message}\n";
        }

        /// <summary>
        /// Finds the owning patient ID and study UID for a series by walking the patient hierarchy.
        /// Returns ("Unknown", "") if the series is not found under any loaded patient.
        /// </summary>
        private (string patientId, string studyUid) ResolveIds(SeriesGroupViewModel seriesVm)
        {
            foreach (var p in Patients)
                foreach (var s in p.Studies)
                    if (s.ImageSeries.Contains(seriesVm))
                        return (p.Model.PatientID, s.Model.StudyInstanceUID);
            return ("Unknown", "");
        }

        /// <summary>
        /// Builds one manifest row per series. When <paramref name="anon"/> is non-null the
        /// PatientID/StudyUID/SeriesUID columns carry the anonymization hashes; otherwise they carry
        /// the real identifiers. Spacing and ROI volumes are attached by the raw SeriesInstanceUID.
        /// </summary>
        private List<ManifestRow> BuildManifestRows(
            IEnumerable<SeriesGroupViewModel> series,
            AnonymizationService anon,
            Dictionary<string, double[]> spacingPerSeries,
            Dictionary<string, Dictionary<string, double>> roiVolumesPerSeries)
        {
            var rows = new List<ManifestRow>();
            foreach (var seriesVm in series)
            {
                var (pid, suid) = ResolveIds(seriesVm);
                string seriesUid = seriesVm.Model.SeriesInstanceUID;

                var row = new ManifestRow
                {
                    PatientID = anon != null ? anon.GetPatientHash(pid) : pid,
                    StudyUID = anon != null ? anon.GetStudyHash(suid) : suid,
                    SeriesUID = anon != null ? anon.GetSeriesHash(seriesUid) : seriesUid,
                };

                if (spacingPerSeries != null && spacingPerSeries.TryGetValue(seriesUid, out double[] spacing))
                {
                    row.SpacingX = spacing[0];
                    row.SpacingY = spacing[1];
                    row.SpacingZ = spacing[2];
                }
                if (roiVolumesPerSeries != null && roiVolumesPerSeries.TryGetValue(seriesUid, out Dictionary<string, double> volumes))
                    row.RoiVolumes = volumes;

                rows.Add(row);
            }
            return rows;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
