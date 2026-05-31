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
using CommunityToolkit.Mvvm.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;
using Dicom_RT_images_Csharp.Views;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// Main ViewModel for the forward (DICOM -> NIfTI) workflow. Ported from WPF; the scan /
    /// convert / metadata logic is unchanged. Cross-platform changes: CommunityToolkit commands
    /// (RefreshCommands() raises CanExecuteChanged), IFolderPicker instead of WinForms dialogs,
    /// async ShowDialog for the OutputSpacing / RoiSelection / Settings dialogs, and an
    /// OS-switched folder reveal. The ROI-association editor, anonymization-key editor and Help
    /// window are not ported yet (their buttons log a notice); the underlying association/anon
    /// data still loads from disk and applies during export.
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
            AllDiscoveredRoiNames = new ObservableCollection<string>();

            BrowseInputCommand = new AsyncRelayCommand(BrowseInputAsync);
            BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputAsync);
            ScanCommand = new AsyncRelayCommand(ExecuteScanAsync, () => !IsScanning && !IsConverting);
            ConvertSelectedCommand = new AsyncRelayCommand(ExecuteConvertAsync,
                () => !IsScanning && !IsConverting && Patients.Count > 0);
            CancelCommand = new RelayCommand(Cancel, () => IsScanning || IsConverting);
            ManageAssociationsCommand = new RelayCommand(OpenAssociationsStub);
            SelectRoisCommand = new AsyncRelayCommand(OpenRoiSelectionAsync);
            OpenAnonymizationKeyEditorCommand = new RelayCommand(OpenAnonymizationKeyEditorStub);
            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
            SelectAllPatientsCommand = new RelayCommand(ToggleSelectAllPatients);
            ExportMetaDataCommand = new AsyncRelayCommand(ExecuteExportMetaDataAsync,
                () => !IsScanning && !IsConverting && Patients.Count > 0);
            OpenOutputSpacingCommand = new AsyncRelayCommand(OpenOutputSpacingAsync);
            OpenHelpCommand = new RelayCommand(OpenHelpStub);

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
        }

        public string InputFolder { get { return _inputFolder; } set { _inputFolder = value; OnPropertyChanged(); } }
        public string OutputFolder { get { return _outputFolder; } set { _outputFolder = value; OnPropertyChanged(); } }

        public bool IsScanning { get { return _isScanning; } set { _isScanning = value; OnPropertyChanged(); RefreshCommands(); } }
        public bool IsConverting { get { return _isConverting; } set { _isConverting = value; OnPropertyChanged(); RefreshCommands(); } }

        public double ProgressValue { get { return _progressValue; } set { _progressValue = value; OnPropertyChanged(); } }
        public string StatusText { get { return _statusText; } set { _statusText = value; OnPropertyChanged(); } }
        public string LogText { get { return _logText; } set { _logText = value; OnPropertyChanged(); } }

        public bool ExportImages { get { return _exportImages; } set { _exportImages = value; OnPropertyChanged(); } }
        public bool IncludeStructures { get { return _includeStructures; } set { _includeStructures = value; OnPropertyChanged(); } }
        public bool IncludeDose { get { return _includeDose; } set { _includeDose = value; OnPropertyChanged(); } }
        public bool OnlyExportSpecificRois { get { return _onlyExportSpecificRois; } set { _onlyExportSpecificRois = value; OnPropertyChanged(); } }
        public bool SpecifyOutputSpacing { get { return _specifyOutputSpacing; } set { _specifyOutputSpacing = value; OnPropertyChanged(); } }
        public double OutputSpacingX { get { return _outputSpacingX; } set { _outputSpacingX = value; OnPropertyChanged(); } }
        public double OutputSpacingY { get { return _outputSpacingY; } set { _outputSpacingY = value; OnPropertyChanged(); } }
        public double OutputSpacingZ { get { return _outputSpacingZ; } set { _outputSpacingZ = value; OnPropertyChanged(); } }
        public bool AnonymizeExport { get { return _anonymizeExport; } set { _anonymizeExport = value; OnPropertyChanged(); } }

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
        public IRelayCommand ManageAssociationsCommand { get; }
        public IAsyncRelayCommand SelectRoisCommand { get; }
        public IRelayCommand OpenAnonymizationKeyEditorCommand { get; }
        public IAsyncRelayCommand OpenSettingsCommand { get; }
        public IRelayCommand SelectAllPatientsCommand { get; }
        public IAsyncRelayCommand ExportMetaDataCommand { get; }
        public IAsyncRelayCommand OpenOutputSpacingCommand { get; }
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

                var exportedRoisPerSeries = new Dictionary<string, List<string>>();
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
                        foreach (var s in p.Studies)
                            if (s.ImageSeries.Contains(seriesVm))
                            {
                                patientId = p.Model.PatientID;
                                studyUid = s.Model.StudyInstanceUID;
                                break;
                            }

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

                    var exportedRoiNames = new List<string>();
                    Dictionary<string, double> roiVolumes = null;

                    if (IncludeStructures && model.LinkedRtStruct != null)
                    {
                        progress.Report($"Rasterizing structures: {displayLabel}");

                        if (model.LinkedRtStruct.RoiNames != null)
                        {
                            if (effectiveAssociations != null && effectiveAssociations.Count > 0)
                            {
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
                                        if (!matchedDicomNames.Contains(roiName))
                                            exportedRoiNames.Add(roiName);
                                }
                            }
                            else
                            {
                                exportedRoiNames.AddRange(model.LinkedRtStruct.RoiNames);
                            }
                        }

                        roiVolumes = await Task.Run(() =>
                            _conversionService.ConvertStructToNifti(
                                model.LinkedRtStruct, model, outputDir,
                                effectiveAssociations, effectiveExportUnmatched,
                                false, progress, _cts.Token, targetSpacing)).ConfigureAwait(true);
                    }

                    exportedRoisPerSeries[seriesUid] = exportedRoiNames;
                    if (roiVolumes != null)
                        roiVolumesPerSeries[seriesUid] = roiVolumes;

                    if (IncludeDose && model.LinkedRtDose != null)
                    {
                        progress.Report($"Converting dose: {displayLabel}");
                        await Task.Run(() =>
                            _conversionService.ConvertDoseToNifti(model.LinkedRtDose, outputDir, progress, _cts.Token, targetSpacing)).ConfigureAwait(true);
                    }

                    completed++;
                    ProgressValue = (double)completed / total * 100;
                }

                List<ManifestRow> manifestRows;
                if (AnonymizeExport && anonService != null)
                {
                    anonService.Save();
                    manifestRows = anonService.GetAllManifestRows();
                }
                else
                {
                    manifestRows = new List<ManifestRow>();
                    foreach (var seriesVm in selectedSeries)
                    {
                        var m = seriesVm.Model;
                        string pid = "Unknown", suid = "";
                        foreach (var p in Patients)
                            foreach (var st in p.Studies)
                                if (st.ImageSeries.Contains(seriesVm))
                                {
                                    pid = p.Model.PatientID;
                                    suid = st.Model.StudyInstanceUID;
                                    break;
                                }
                        manifestRows.Add(new ManifestRow { MRN = pid, StudyUID = suid, SeriesUID = m.SeriesInstanceUID, ExportID = -1 });
                    }
                }

                foreach (var row in manifestRows)
                {
                    if (spacingPerSeries.TryGetValue(row.SeriesUID, out double[] spacing))
                    {
                        row.SpacingX = spacing[0];
                        row.SpacingY = spacing[1];
                        row.SpacingZ = spacing[2];
                    }
                    if (roiVolumesPerSeries.TryGetValue(row.SeriesUID, out Dictionary<string, double> volumes))
                        row.RoiVolumes = volumes;
                }

                var allExportedRoiNames = new List<string>();
                if (IncludeStructures)
                {
                    var roiNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rois in exportedRoisPerSeries.Values)
                        foreach (var name in rois)
                            if (roiNameSet.Add(name))
                                allExportedRoiNames.Add(name);
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
                    string patientId = "Unknown", studyUid = "", seriesUid = model.SeriesInstanceUID;

                    foreach (var p in Patients)
                        foreach (var s in p.Studies)
                            if (s.ImageSeries.Contains(seriesVm))
                            {
                                patientId = p.Model.PatientID;
                                studyUid = s.Model.StudyInstanceUID;
                                break;
                            }

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

                var manifestRows = new List<ManifestRow>();
                foreach (var seriesVm in selectedSeries)
                {
                    var m = seriesVm.Model;
                    string pid = "Unknown", suid = "";
                    foreach (var p in Patients)
                        foreach (var st in p.Studies)
                            if (st.ImageSeries.Contains(seriesVm))
                            {
                                pid = p.Model.PatientID;
                                suid = st.Model.StudyInstanceUID;
                                break;
                            }

                    var row = new ManifestRow { MRN = pid, StudyUID = suid, SeriesUID = m.SeriesInstanceUID, ExportID = -1 };
                    if (spacingPerSeries.TryGetValue(row.SeriesUID, out double[] spacing))
                    {
                        row.SpacingX = spacing[0];
                        row.SpacingY = spacing[1];
                        row.SpacingZ = spacing[2];
                    }
                    if (roiVolumesPerSeries.TryGetValue(row.SeriesUID, out Dictionary<string, double> volumes))
                        row.RoiVolumes = volumes;
                    manifestRows.Add(row);
                }

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
        {
            if (associations == null || associations.Count == 0) return rawName;
            foreach (var assoc in associations)
            {
                if (string.Equals(assoc.CanonicalName, rawName, StringComparison.OrdinalIgnoreCase))
                    return assoc.CanonicalName;
                foreach (var alias in assoc.Aliases)
                    if (string.Equals(alias, rawName, StringComparison.OrdinalIgnoreCase))
                        return assoc.CanonicalName;
            }
            return rawName;
        }

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

        // --- Editors not yet ported to Avalonia (next Phase 4 increments). The underlying
        //     association / anonymization data still loads from disk and applies during export. ---
        private void OpenAssociationsStub()
        {
            StatusText = "ROI Associations editor is not yet ported.";
            AppendLog("ROI Associations editor is coming in a later increment. Existing associations in " +
                      "%AppData%/DicomToNifti/roi_associations.json still apply (renaming) during export.");
        }

        private void OpenAnonymizationKeyEditorStub()
        {
            StatusText = "Anonymization-key editor is not yet ported.";
            AppendLog("The Anonymization-key editor is coming in a later increment. Anonymized exports still " +
                      "work and write/extend AnonymizationKey.json in the output folder.");
        }

        private void OpenHelpStub()
        {
            StatusText = "Help window is not yet ported.";
            AppendLog("The in-app Help window is coming in a later increment.");
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

        private void WriteCsvManifest(List<ManifestRow> rows, string outputFolder, List<string> roiColumnNames = null, string fileName = "export_manifest.csv")
        {
            string csvPath = Path.Combine(outputFolder, fileName);
            using (var writer = new StreamWriter(csvPath))
            {
                var header = "MRN,StudyUID,SeriesUID,ExportID,SpacingX,SpacingY,SpacingZ";
                if (roiColumnNames != null && roiColumnNames.Count > 0)
                    foreach (var roiName in roiColumnNames)
                        header += ",\"" + roiName.Replace("\"", "\"\"") + "\"";
                writer.WriteLine(header);

                foreach (var row in rows)
                {
                    var line = string.Format("\"{0}\",\"{1}\",\"{2}\",{3},{4},{5},{6}",
                        row.MRN, row.StudyUID, row.SeriesUID, row.ExportID, row.SpacingX, row.SpacingY, row.SpacingZ);
                    if (roiColumnNames != null && roiColumnNames.Count > 0)
                        foreach (var roiName in roiColumnNames)
                            line += (row.RoiVolumes != null && row.RoiVolumes.TryGetValue(roiName, out double volume)) ? "," + volume : ",-1";
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

        private static string SanitizePath(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) name = name.Replace(c, '_');
            return name;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
