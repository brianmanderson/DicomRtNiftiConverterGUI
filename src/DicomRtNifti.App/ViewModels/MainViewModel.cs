using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.App.Services;
using DicomRtNifti.App.Views;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// Main ViewModel for the forward (DICOM -> NIfTI) workflow. Cross-platform changes from the
    /// WPF port: CommunityToolkit commands (RefreshCommands() raises CanExecuteChanged),
    /// IFolderPicker instead of WinForms dialogs, async ShowDialog for the OutputSpacing /
    /// RoiSelection / Settings / ROI-association / anonymization-key dialogs, and an OS-switched
    /// folder reveal. The Help and Export Options windows open non-modally via Show(); ROI
    /// associations load from disk and apply during export.
    ///
    /// It owns no export logic. Convert Selected and Export Manifest Only turn the window's state
    /// into a <see cref="CohortExportOptions"/> (<see cref="ExportOptionsFor"/>), let
    /// <see cref="CohortExportService.BuildPlan"/> decide what is written where, show that plan
    /// (<see cref="ExportPlanViewModel"/>), and run it through
    /// <see cref="CohortExportService.ExecuteAsync"/> or
    /// <see cref="CohortExportService.ComputeManifestAsync"/>: the CLI's cohort modes, so the two
    /// front-ends name, anonymize, select and write alike (W3-D.2).
    ///
    /// Every export toggle is written to settings.json the moment it changes, the series-selection
    /// controls and the input and output folders to gui_state.json (<see cref="GuiStateStore"/>),
    /// so a window closed without converting still reopens as it was left. Selecting a patient,
    /// study or series in the tree shows it in the inspector panel
    /// (<see cref="PatientInspectorViewModel"/>, <see cref="StudyInspectorViewModel"/>,
    /// <see cref="SeriesInspectorViewModel"/>).
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        /// <summary>The manifest Export Manifest Only writes, beside Convert Selected's export_manifest.csv.</summary>
        public const string SurveyManifestFileName = "export_manifest_meta.csv";

        private readonly DicomScannerService _scannerService;
        private readonly CohortExportService _cohortExportService;
        private readonly SettingsService _settingsService;
        private readonly IFolderPicker _folderPicker;
        private readonly GuiStateStore _guiState;
        private bool _guiStateErrorLogged;
        private object _selectedTreeItem;
        private InspectorViewModel _selectedInspector;

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

        // The last scan, which the tree shows and every plan is built from, and the folder it read.
        private DicomScanResult _scan;
        private string _scannedFolder;

        // The non-modal Export Options companion window, tracked so a second click re-focuses the
        // open window instead of stacking duplicates. Null whenever the window is closed.
        private ExportOptionsWindow _exportOptionsWindow;

        private bool _exportImages = true;
        private bool _includeStructures = true;
        private bool _includeDose = true;
        private bool _onlyExportSpecificRois;
        private bool _anonymizeExport;
        private string _anonymizationKeyPath = "";
        private bool _allPatientsSelected = true;
        private bool _specifyOutputSpacing;
        private double _outputSpacingX = 1.0;
        private double _outputSpacingY = 1.0;
        private double _outputSpacingZ = 1.0;
        private bool _exportDicomMetadata;
        private List<string> _metadataImageTagKeywords = new List<string>();
        private List<string> _metadataStructureTagKeywords = new List<string>();
        private List<string> _metadataDoseTagKeywords = new List<string>();
        private bool _requireStructures;
        private bool _requireDose;
        private string _seriesDescriptionFilter = "";
        private string _structDescriptionFilter = "";

        private HashSet<string> _selectedRoiNames;

        public MainViewModel(
            DicomScannerService scannerService,
            NiftiConversionService conversionService,
            SettingsService settingsService,
            IFolderPicker folderPicker,
            GuiStateStore guiState = null)
        {
            _scannerService = scannerService;
            _cohortExportService = new CohortExportService(conversionService);
            _settingsService = settingsService;
            _folderPicker = folderPicker;
            _guiState = guiState ?? new GuiStateStore();

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
            BrowseAnonymizationKeyFolderCommand = new AsyncRelayCommand(BrowseAnonymizationKeyFolderAsync);
            OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
            SelectAllPatientsCommand = new RelayCommand(ToggleSelectAllPatients);
            ExportMetaDataCommand = new AsyncRelayCommand(ExecuteExportMetaDataAsync,
                () => !IsScanning && !IsConverting && Patients.Count > 0 && !string.IsNullOrWhiteSpace(OutputFolder));
            OpenOutputSpacingCommand = new AsyncRelayCommand(OpenOutputSpacingAsync);
            OpenExportOptionsCommand = new RelayCommand(OpenExportOptions);
            OpenMetadataTagsCommand = new AsyncRelayCommand(OpenMetadataTagsAsync);
            OpenHelpCommand = new RelayCommand(OpenHelp);

            _settings = LoadSettingsGuarded();
            _associations = LoadAssociationsGuarded();

            // The last folders used win over Settings' default output directory, which remains
            // the fallback until another output folder has been used. A remembered folder that no
            // longer exists is not restored.
            var gui = _guiState.Load();
            if (_guiState.IsBlocked)
                NoteGuiStateNotSaved(_guiState.BlockedReason);
            if (Directory.Exists(gui.LastInputFolder ?? ""))
                _inputFolder = gui.LastInputFolder;
            if (Directory.Exists(gui.LastOutputFolder ?? ""))
                _outputFolder = gui.LastOutputFolder;
            else if (!string.IsNullOrEmpty(_settings.DefaultOutputDirectory))
                _outputFolder = _settings.DefaultOutputDirectory;
            _requireStructures = gui.RequireStructures;
            _requireDose = gui.RequireDose;
            _seriesDescriptionFilter = gui.SeriesDescriptionFilter ?? "";
            _structDescriptionFilter = gui.StructDescriptionFilter ?? "";

            _exportImages = _settings.ExportImages;
            _includeStructures = _settings.IncludeStructures;
            _includeDose = _settings.IncludeDose;
            _onlyExportSpecificRois = _settings.OnlyExportSpecificRois;
            _anonymizeExport = _settings.AnonymizeExport;
            _anonymizationKeyPath = _settings.AnonymizationKeyPath ?? "";
            _specifyOutputSpacing = _settings.SpecifyOutputSpacing;
            _outputSpacingX = _settings.OutputSpacingX;
            _outputSpacingY = _settings.OutputSpacingY;
            _outputSpacingZ = _settings.OutputSpacingZ;
            _exportDicomMetadata = _settings.ExportDicomMetadata;
            _metadataImageTagKeywords = _settings.MetadataImageTagKeywords ?? new List<string>();
            _metadataStructureTagKeywords = _settings.MetadataStructureTagKeywords ?? new List<string>();
            _metadataDoseTagKeywords = _settings.MetadataDoseTagKeywords ?? new List<string>();
        }

        public string InputFolder
        {
            get { return _inputFolder; }
            set { _inputFolder = value; OnPropertyChanged(); RefreshCommands(); RememberFolder(value, s => s.LastInputFolder = value); }
        }

        public string OutputFolder
        {
            get { return _outputFolder; }
            set { _outputFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(AnonymizationKeyLocationText)); RefreshCommands(); RememberFolder(value, s => s.LastOutputFolder = value); }
        }

        /// <summary>True when no scan results are loaded yet — drives the tree's empty-state hint.</summary>
        public bool ShowTreeEmptyHint => Patients.Count == 0;

        /// <summary>The tree's selected node (patient, study or series), bound two-way to the TreeView.</summary>
        public object SelectedTreeItem
        {
            get { return _selectedTreeItem; }
            set
            {
                _selectedTreeItem = value;
                OnPropertyChanged();
                SelectedInspector = InspectorFor(value);
            }
        }

        /// <summary>
        /// What the inspector panel shows: the selected patient, study or series, each built on its
        /// first selection, or null (and the panel's hint) when nothing is selected. Selecting a row
        /// starts reading its tag grid in the background.
        /// </summary>
        public InspectorViewModel SelectedInspector
        {
            get { return _selectedInspector; }
            private set
            {
                _selectedInspector = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowInspectorHint));
                OnPropertyChanged(nameof(InspectorHeader));
                if (value != null)
                    _ = value.EnsureTagsLoadedAsync();
            }
        }

        public bool ShowInspectorHint => SelectedInspector == null;

        /// <summary>"Patient inspector", "Study inspector" or "Series inspector" for the selected level; "Inspector" with none.</summary>
        public string InspectorHeader => SelectedInspector?.Header ?? "Inspector";

        private static InspectorViewModel InspectorFor(object treeItem)
        {
            switch (treeItem)
            {
                case PatientGroupViewModel patient: return patient.Inspector;
                case StudyGroupViewModel study: return study.Inspector;
                case SeriesGroupViewModel series: return series.Inspector;
                default: return null;
            }
        }

        public bool IsScanning { get { return _isScanning; } set { _isScanning = value; OnPropertyChanged(); RefreshCommands(); } }
        public bool IsConverting { get { return _isConverting; } set { _isConverting = value; OnPropertyChanged(); RefreshCommands(); } }

        public double ProgressValue { get { return _progressValue; } set { _progressValue = value; OnPropertyChanged(); } }
        public string StatusText { get { return _statusText; } set { _statusText = value; OnPropertyChanged(); } }
        public string LogText { get { return _logText; } set { _logText = value; OnPropertyChanged(); } }

        // The toggles below persist on change (PersistSetting); only a real change writes.
        public bool ExportImages
        {
            get { return _exportImages; }
            set { if (_exportImages == value) return; _exportImages = value; OnPropertyChanged(); PersistSetting(s => s.ExportImages = value); }
        }
        public bool IncludeStructures
        {
            get { return _includeStructures; }
            set { if (_includeStructures == value) return; _includeStructures = value; OnPropertyChanged(); PersistSetting(s => s.IncludeStructures = value); }
        }
        public bool IncludeDose
        {
            get { return _includeDose; }
            set { if (_includeDose == value) return; _includeDose = value; OnPropertyChanged(); PersistSetting(s => s.IncludeDose = value); }
        }
        public bool OnlyExportSpecificRois
        {
            get { return _onlyExportSpecificRois; }
            set { if (_onlyExportSpecificRois == value) return; _onlyExportSpecificRois = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistSetting(s => s.OnlyExportSpecificRois = value); }
        }
        public bool SpecifyOutputSpacing
        {
            get { return _specifyOutputSpacing; }
            set { if (_specifyOutputSpacing == value) return; _specifyOutputSpacing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistSetting(s => s.SpecifyOutputSpacing = value); }
        }
        public double OutputSpacingX { get { return _outputSpacingX; } set { _outputSpacingX = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public double OutputSpacingY { get { return _outputSpacingY; } set { _outputSpacingY = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public double OutputSpacingZ { get { return _outputSpacingZ; } set { _outputSpacingZ = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); } }
        public bool AnonymizeExport
        {
            get { return _anonymizeExport; }
            set { if (_anonymizeExport == value) return; _anonymizeExport = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistSetting(s => s.AnonymizeExport = value); }
        }

        /// <summary>
        /// Where an anonymized export writes AnonymizationKey.json: a file, or a folder to hold it.
        /// Empty for the default folder beside the export (<see cref="AnonymizationKeyLocation"/>).
        /// </summary>
        public string AnonymizationKeyPath
        {
            get { return _anonymizationKeyPath; }
            set
            {
                value = value ?? "";
                if (_anonymizationKeyPath == value) return;
                _anonymizationKeyPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnonymizationKeyLocationText));
                PersistSetting(s => s.AnonymizationKeyPath = value);
            }
        }

        /// <summary>Where the key of an anonymized export to the current output folder would go, or why it cannot.</summary>
        public string AnonymizationKeyLocationText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(OutputFolder))
                    return "Choose an output folder to see where the key will be written.";
                try
                {
                    return "Key file: " + AnonymizationKeyLocation.Resolve(OutputFolder, AnonymizationKeyPath);
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }
        }
        public bool ExportDicomMetadata
        {
            get { return _exportDicomMetadata; }
            set { if (_exportDicomMetadata == value) return; _exportDicomMetadata = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistSetting(s => s.ExportDicomMetadata = value); }
        }

        // The series-selection controls (Export Options): CohortExportOptions' RequireStructures,
        // RequireDose and description filters, persisted to gui_state.json on change.
        public bool RequireStructures
        {
            get { return _requireStructures; }
            set { if (_requireStructures == value) return; _requireStructures = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistGuiState(s => s.RequireStructures = value); }
        }
        public bool RequireDose
        {
            get { return _requireDose; }
            set { if (_requireDose == value) return; _requireDose = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportOptionsSummary)); PersistGuiState(s => s.RequireDose = value); }
        }

        /// <summary>Only series whose SeriesDescription contains this (case-insensitive) are exported; empty for all.</summary>
        public string SeriesDescriptionFilter
        {
            get { return _seriesDescriptionFilter; }
            set
            {
                value = value ?? "";
                if (_seriesDescriptionFilter == value) return;
                _seriesDescriptionFilter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExportOptionsSummary));
                PersistGuiState(s => s.SeriesDescriptionFilter = value);
            }
        }

        /// <summary>
        /// Only series with a linked structure set whose SeriesDescription contains this
        /// (case-insensitive) are exported, with that structure set; empty for all.
        /// </summary>
        public string StructDescriptionFilter
        {
            get { return _structDescriptionFilter; }
            set
            {
                value = value ?? "";
                if (_structDescriptionFilter == value) return;
                _structDescriptionFilter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExportOptionsSummary));
                PersistGuiState(s => s.StructDescriptionFilter = value);
            }
        }

        /// <summary>
        /// The window this view-model's dialogs open over when no window is active (a headless
        /// session has no desktop lifetime to ask): the forward window, which sets it to itself.
        /// </summary>
        public Window DialogOwner { get; set; }

        /// <summary>The owner for a dialog: the active window (Export Options, say), else <see cref="DialogOwner"/>.</summary>
        private Window Owner => AppWindows.Active ?? DialogOwner;

        /// <summary>
        /// One-line digest of the non-default Export Options, shown next to the Export Options
        /// button so hidden state (which lives in a separate window) is visible at a glance.
        /// </summary>
        public string ExportOptionsSummary
        {
            get
            {
                var parts = new List<string>();
                if (RequireStructures || RequireDose
                    || !string.IsNullOrWhiteSpace(SeriesDescriptionFilter)
                    || !string.IsNullOrWhiteSpace(StructDescriptionFilter))
                    parts.Add("series filter");
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
        public IAsyncRelayCommand BrowseAnonymizationKeyFolderCommand { get; }
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

        private async Task BrowseAnonymizationKeyFolderAsync()
        {
            string picked = await _folderPicker.PickFolderAsync(
                "Select a folder for AnonymizationKey.json (outside the export)", AnonymizationKeyPath);
            if (!string.IsNullOrEmpty(picked)) AnonymizationKeyPath = picked;
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
            SelectedTreeItem = null;
            Patients.Clear();
            AllDiscoveredRoiNames.Clear();
            _scan = null;
            _scannedFolder = null;
            ProgressValue = 0;
            RememberFolder(InputFolder, s => s.LastInputFolder = InputFolder);

            // The status line follows the scanner's log; the bar its structured counts. Reads
            // finish on many threads, so two counts can arrive out of order: keep the high-water
            // marks (the last event is always the final count).
            var progress = new Progress<string>(msg => StatusText = msg);
            int filesRead = 0, filesFound = 0;
            var scanProgress = new Progress<ScanProgress>(p =>
            {
                filesRead = Math.Max(filesRead, p.FilesProcessed);
                filesFound = Math.Max(filesFound, p.FilesFound);
                if (filesFound > 0)
                    ProgressValue = 100.0 * Math.Min(filesRead, filesFound) / filesFound;
            });

            try
            {
                AppendLog($"Scanning {InputFolder}...");
                string folder = InputFolder;
                var scanResult = await Task.Run(() =>
                    _scannerService.ScanFolderAsync(folder, progress, _cts.Token, scanProgress)).ConfigureAwait(true);
                _scan = scanResult;
                _scannedFolder = folder;

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
                LogScanGeometryNotes();
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

        /// <summary>
        /// Says at scan time what used to surface only on Convert: how many series have mixed
        /// slice gaps, and how many carry an RT object linked by the largest-series fallback.
        /// Both are badged in the tree; the inspector has the detail.
        /// </summary>
        private void LogScanGeometryNotes()
        {
            var series = Patients.SelectMany(p => p.Studies).SelectMany(s => s.ImageSeries).ToList();

            int nonUniform = series.Count(s => s.IsNonUniformZ);
            if (nonUniform > 0)
                AppendLog($"{nonUniform} series ha{(nonUniform == 1 ? "s" : "ve")} non-uniform slice spacing (badged \"non-uniform Z\"). " +
                          "NIfTI stores one spacing per axis, so exporting flattens the gaps; select the series to see them.");

            int guessed = series.Count(s => s.HasGuessedLink);
            if (guessed > 0)
                AppendLog($"{guessed} series ha{(guessed == 1 ? "s" : "ve")} an RTSTRUCT or RTDOSE linked by the largest-series fallback (badged \"link guessed\"): " +
                          "nothing in the RT object identified its image series. Check it in the inspector before exporting.");
        }

        /// <summary>
        /// Every ROI name of every linked structure set in the scan, not only the first set of each
        /// series, since any of them can be picked for export.
        /// </summary>
        private void AggregateDiscoveredRoiNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        foreach (var roiName in series.AllRoiNames)
                            names.Add(roiName);

            AllDiscoveredRoiNames.Clear();
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                AllDiscoveredRoiNames.Add(name);
        }

        /// <summary>
        /// Counts how many series each discovered ROI name appears in (case-insensitive, keeping the
        /// first-seen casing), over every linked structure set. Feeds the ROI Associations browser
        /// so it can show "Name (count)".
        /// </summary>
        private Dictionary<string, int> ComputeDiscoveredRoiCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        foreach (var roiName in series.AllRoiNames)
                            counts[roiName] = counts.TryGetValue(roiName, out int c) ? c + 1 : 1;
            return counts;
        }

        private Task ExecuteConvertAsync() => RunCohortAsync(CohortMode.Convert);

        private Task ExecuteExportMetaDataAsync() => RunCohortAsync(CohortMode.Manifest);

        /// <summary>
        /// The image series the window would export: every ticked series of every ticked patient,
        /// in tree order. A series ticked under an unticked patient is not exported.
        /// </summary>
        public List<SeriesGroupViewModel> SelectedSeries()
        {
            var selected = new List<SeriesGroupViewModel>();
            foreach (var patient in Patients)
            {
                if (!patient.IsSelected) continue;
                foreach (var study in patient.Studies)
                    foreach (var series in study.ImageSeries)
                        if (series.IsSelected)
                            selected.Add(series);
            }
            return selected;
        }

        /// <summary>
        /// The window's state as the <see cref="CohortExportOptions"/> CohortExportService runs:
        /// the ticked patients and series, each picked structure set, the Require switches and
        /// description filters, the Data to export toggles, the ROI associations and selection
        /// ("Limit export to selected ROIs" maps to <see cref="CohortExportOptions.OnlyAssociatedRois"/>
        /// and, with ROIs selected, to <see cref="CohortExportOptions.RoiSelection"/>), the output
        /// spacing, anonymization and key location, and the metadata tags when the sidecar is on.
        /// A mapping, not a decision: what the options mean is Core's.
        /// </summary>
        public CohortExportOptions ExportOptionsFor(CohortMode mode)
        {
            var selected = SelectedSeries();
            return new CohortExportOptions
            {
                InputRoot = _scannedFolder ?? InputFolder,
                OutputRoot = OutputFolder,
                // The ticked patients bound the plan, so an unticked patient's series are not listed
                // as skipped; within them, the ticked series.
                PatientIds = Patients.Where(p => p.IsSelected)
                    .Select(p => p.Model.PatientID ?? "")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                SeriesInstanceUids = selected.Select(s => s.Model.SeriesInstanceUID ?? "").ToList(),
                SeriesDescriptionFilter = Blank(SeriesDescriptionFilter),
                StructDescriptionFilter = Blank(StructDescriptionFilter),
                StructureSetUids = selected
                    .Where(s => s.ChosenStructureSet != null)
                    .Select(s => s.ChosenStructureSet.SeriesInstanceUID ?? "")
                    .ToList(),
                RequireStructures = RequireStructures,
                RequireDose = RequireDose,
                ExportImages = ExportImages,
                ExportStructures = IncludeStructures,
                ExportDoses = IncludeDose,
                Associations = _associations ?? new List<RoiAssociation>(),
                OnlyAssociatedRois = OnlyExportSpecificRois,
                RoiSelection = OnlyExportSpecificRois && _selectedRoiNames != null
                    ? _selectedRoiNames.ToList()
                    : new List<string>(),
                OutputSpacing = SpecifyOutputSpacing
                    ? new[] { OutputSpacingX, OutputSpacingY, OutputSpacingZ }
                    : null,
                Anonymize = AnonymizeExport,
                KeyPath = Blank(AnonymizationKeyPath),
                Salt = _settings.HashSalt,
                ImageKeywords = ExportDicomMetadata ? new List<string>(_metadataImageTagKeywords) : new List<string>(),
                StructureKeywords = ExportDicomMetadata ? new List<string>(_metadataStructureTagKeywords) : new List<string>(),
                DoseKeywords = ExportDicomMetadata ? new List<string>(_metadataDoseTagKeywords) : new List<string>(),
                ManifestFileName = mode == CohortMode.Convert ? ExportManifestService.DefaultFileName : SurveyManifestFileName,
            };
        }

        private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        /// <summary>
        /// Convert Selected (<see cref="CohortMode.Convert"/>) and Export Manifest Only
        /// (<see cref="CohortMode.Manifest"/>): plan the window's selection, show the plan, and on
        /// the user's go-ahead run it through CohortExportService, with the log taking the service's
        /// own messages and the progress bar and status line its structured events.
        /// </summary>
        private async Task RunCohortAsync(CohortMode mode)
        {
            bool convert = mode == CohortMode.Convert;
            string noun = convert ? "Conversion" : "Metadata export";

            if (string.IsNullOrEmpty(OutputFolder))
            {
                AppendLog("Error: Please select an output folder.");
                StatusText = "Please select an output folder.";
                return;
            }
            if (_scan == null || SelectedSeries().Count == 0)
            {
                // An empty series selection would mean "every series" to the service.
                AppendLog("No series selected for export.");
                StatusText = "No series selected.";
                return;
            }

            if (convert)
            {
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
                SaveSettingsGuarded();
            }

            _associations = LoadAssociationsGuarded();
            var options = ExportOptionsFor(mode);

            // The plan to review, hashed (when anonymizing) with the key read where the run will
            // write it and saved nowhere, as the CLI's --dry-run does: reviewing writes nothing.
            ExportPlanViewModel review;
            try
            {
                AnonymizationService previewKey = null;
                string keyFile = null, legacyKeyWarning = null;
                if (options.Anonymize)
                    previewKey = ReadOnlyAnonymizationKey.OpenForExport(
                        options.OutputRoot, options.KeyPath, options.Salt, out keyFile, out legacyKeyWarning);
                var plan = CohortExportService.BuildPlan(_scan, options, previewKey);
                review = new ExportPlanViewModel(mode, plan, options, previewKey, keyFile, legacyKeyWarning);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is InvalidOperationException)
            {
                AppendLog($"Cannot open the anonymization key: {ex.Message}");
                StatusText = $"{noun} not started.";
                return;
            }

            if (!await ShowPlanAsync(review).ConfigureAwait(true))
            {
                AppendLog($"{noun} not started: the plan was closed without running it.");
                StatusText = "Ready";
                return;
            }

            Directory.CreateDirectory(OutputFolder);
            RememberFolder(OutputFolder, s => s.LastOutputFolder = OutputFolder);

            IsConverting = true;
            _cts = new CancellationTokenSource();
            ProgressValue = 0;

            // Both channels post to this thread in the order the run sends them: the log is the
            // service's (the CLI's stderr), the bar and status line its structured events.
            IProgress<string> log = new Progress<string>(AppendLog);
            IProgress<CohortProgress> events = new Progress<CohortProgress>(e => ShowProgress(e, convert));

            try
            {
                // Beside the export or at the chosen key location, never inside it (#54). The plan is
                // built again against the key the run saves, so the mappings it records are the
                // ones written; the hashes are the reviewed ones (they are deterministic).
                AnonymizationService anon = options.Anonymize ? OpenAnonymizationKey() : null;
                var runPlan = CohortExportService.BuildPlan(_scan, options, anon);
                LogPlan(runPlan, convert);

                var token = _cts.Token;
                var result = await Task.Run(() => convert
                    ? _cohortExportService.ExecuteAsync(runPlan, options, anon, log, token, events)
                    : _cohortExportService.ComputeManifestAsync(runPlan, options, anon, log, token, events), token)
                    .ConfigureAwait(true);

                LogResult(result, convert);

                if (convert && _settings.AutoOpenAfterConversion)
                    RevealFolder(OutputFolder);
            }
            catch (OperationCanceledException)
            {
                AppendLog($"{noun} cancelled.");
                StatusText = "Cancelled.";
            }
            catch (Exception ex)
            {
                AppendLog($"{noun} error: {ex.Message}");
                StatusText = $"{noun} failed.";
            }
            finally
            {
                IsConverting = false;
                _cts?.Dispose();
                _cts = null;
                RefreshCommands();
            }
        }

        /// <summary>Shows the plan modally; true when the user runs it.</summary>
        private Task<bool> ShowPlanAsync(ExportPlanViewModel review)
        {
            var owner = Owner;
            if (owner == null)
            {
                AppendLog("There is no window to show the export plan in.");
                return Task.FromResult(false);
            }
            return new ExportPlanWindow { DataContext = review }.ShowDialog<bool>(owner);
        }

        /// <summary>The status line and progress bar, from the run's structured events.</summary>
        private void ShowProgress(CohortProgress e, bool convert)
        {
            switch (e.Phase)
            {
                case CohortProgressPhase.SeriesStarted:
                    StatusText = $"{(convert ? "Converting" : "Surveying")} series {e.SeriesIndex} of {e.SeriesCount}: {e.SeriesLabel}";
                    break;
                case CohortProgressPhase.SeriesFinished:
                    if (e.SeriesCount > 0)
                        ProgressValue = 100.0 * e.SeriesIndex / e.SeriesCount;
                    break;
                case CohortProgressPhase.Completed:
                    ProgressValue = 100;
                    break;
            }
        }

        /// <summary>What the run is about to do, as the CLI's cohort modes print it, and every series it leaves out.</summary>
        private void LogPlan(CohortExportPlan plan, bool convert)
        {
            AppendLog($"{(convert ? "Converting" : "Surveying")} {plan.Series.Count} series into {OutputFolder} " +
                      $"({plan.Skipped.Count} skipped, {plan.UnlinkedRtObjects.Count} unlinked RT objects)");
            foreach (var skipped in plan.Skipped)
                AppendLog($"  skipped {skipped.PatientId}/{ExportPlanViewModel.Short(skipped.SeriesInstanceUid)} " +
                          $"({skipped.Modality} {skipped.SeriesDescription}): {skipped.Reason}");
        }

        /// <summary>
        /// The run's outcome in the log and the status line: what was written, every series that
        /// failed with its message, and where the key went.
        /// </summary>
        private void LogResult(CohortExportResult result, bool convert)
        {
            string noun = convert ? "Conversion" : "Metadata export";
            AppendLog($"Wrote manifest: {Path.Combine(result.OutputRoot, result.ManifestCsv)}");

            // A series' error names its series; the run's own (an ROI selection that matched no
            // ROI anywhere) names none, and the service has already logged it as a warning.
            var failures = result.Errors
                .Where(e => !string.IsNullOrEmpty(e.SeriesUid) || !string.IsNullOrEmpty(e.PatientId))
                .ToList();
            bool runWarning = result.Errors.Count > failures.Count;
            int failed = result.FailedCount;

            AppendLog(convert
                ? $"Conversion complete. {result.SucceededCount} series exported to {OutputFolder}" +
                  (failed > 0 ? $"; {failed} failed and are not in the manifest:" : "")
                : $"Metadata export complete. {result.SucceededCount} series processed." +
                  (failed > 0 ? $" {failed} failed and are not in the manifest:" : ""));
            foreach (var failure in failures)
                AppendLog($"  FAILED {failure.PatientId}/{ExportPlanViewModel.Short(failure.SeriesUid)}: {failure.Message}");

            if (result.Anonymized && !string.IsNullOrEmpty(result.AnonymizationKey))
                AppendLog($"Anonymization key (outside the export; keep it, do not share it): {result.AnonymizationKey}");

            StatusText = failed > 0
                ? $"{noun} complete; {failed} series failed."
                : runWarning
                    ? $"{noun} complete, with a warning (see the log)."
                    : $"{noun} complete.";
        }

        private void ToggleSelectAllPatients() => AllPatientsSelected = !AllPatientsSelected;

        private void Cancel() => _cts?.Cancel();

        private async Task OpenOutputSpacingAsync()
        {
            var window = new OutputSpacingWindow(OutputSpacingX, OutputSpacingY, OutputSpacingZ);
            bool ok = await window.ShowDialog<bool>(Owner);
            if (ok)
            {
                OutputSpacingX = window.SpacingX;
                OutputSpacingY = window.SpacingY;
                OutputSpacingZ = window.SpacingZ;
                _settings.OutputSpacingX = OutputSpacingX;
                _settings.OutputSpacingY = OutputSpacingY;
                _settings.OutputSpacingZ = OutputSpacingZ;
                SaveSettingsGuarded();
                AppendLog($"Output spacing set to {OutputSpacingX} x {OutputSpacingY} x {OutputSpacingZ} mm");
            }
        }

        private async Task OpenRoiSelectionAsync()
        {
            _associations = LoadAssociationsGuarded();
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

            if (await window.ShowDialog<bool>(Owner))
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
            // The dialog writes settings.json on Save; don't offer it while an unreadable file is
            // being preserved, or that Save would overwrite it with in-memory defaults.
            if (_settingsPersistenceBlocked)
            {
                AppendLog("Settings cannot be edited while an unreadable settings file is being preserved.");
                return;
            }

            var window = new SettingsWindow(_settingsService, _settings);
            if (await window.ShowDialog<bool>(Owner))
            {
                _settings = LoadSettingsGuarded();
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
            await window.ShowDialog<bool>(Owner);

            // The editor persists on Save; reload so a subsequent export/ROI-selection picks up edits.
            _associations = LoadAssociationsGuarded();
            AppendLog($"ROI Associations editor closed. {_associations.Count} association(s) loaded.");
        }

        private async Task OpenAnonymizationKeyEditorAsync()
        {
            // Edit the key file that an anonymized export to the current output folder would use,
            // outside the export (#54); a key an earlier export left inside the folder is merged
            // into it first. With no output folder: the chosen key location, else the %AppData%
            // copy (inspection only).
            string keyFilePath;
            try
            {
                if (!string.IsNullOrEmpty(OutputFolder))
                    keyFilePath = OpenAnonymizationKey().KeyFilePath;
                else if (!string.IsNullOrWhiteSpace(AnonymizationKeyPath))
                    keyFilePath = AnonymizationKeyLocation.NormalizeKeyPath(AnonymizationKeyPath);
                else
                    keyFilePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DicomToNifti", AnonymizationKeyLocation.FileName);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is InvalidOperationException)
            {
                AppendLog($"Cannot open the anonymization key: {ex.Message}");
                return;
            }

            var vm = new AnonymizationKeyEditorViewModel(keyFilePath, _settings.HashSalt);
            var window = new AnonymizationKeyEditorWindow { DataContext = vm };
            if (await window.ShowDialog<bool>(Owner))
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

            var owner = Owner;
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
            if (await window.ShowDialog<bool>(Owner))
            {
                _metadataImageTagKeywords = vm.ImagesSection.GetSelectedKeywords();
                _metadataStructureTagKeywords = vm.StructuresSection.GetSelectedKeywords();
                _metadataDoseTagKeywords = vm.DoseSection.GetSelectedKeywords();
                _settings.ExportDicomMetadata = ExportDicomMetadata;
                _settings.MetadataImageTagKeywords = _metadataImageTagKeywords;
                _settings.MetadataStructureTagKeywords = _metadataStructureTagKeywords;
                _settings.MetadataDoseTagKeywords = _metadataDoseTagKeywords;
                SaveSettingsGuarded();
                AppendLog($"Metadata tags selected: {_metadataImageTagKeywords.Count} image, " +
                          $"{_metadataStructureTagKeywords.Count} structure, {_metadataDoseTagKeywords.Count} dose.");
            }
        }

        private void OpenHelp()
        {
            var help = new DicomToNiftiHelpWindow();
            var owner = Owner;
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

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogText += $"[{timestamp}] {message}\n";
        }

        // ── settings / associations persistence guards ─────────────────────────────────────
        //
        // SettingsService now throws when settings.json or roi_associations.json exists but does
        // not parse, instead of quietly handing back defaults that the next save would write over
        // the damaged original. The GUI must stay usable in that state, so it falls back to
        // defaults *in memory only* and stops writing to disk until the user has dealt with the
        // file — turning silent data loss into a visible, recoverable message.

        private bool _settingsPersistenceBlocked;

        private AppSettings LoadSettingsGuarded()
        {
            try { return _settingsService.LoadSettings(); }
            catch (InvalidDataException ex) { BlockSettingsPersistence(ex); return new AppSettings(); }
        }

        private List<RoiAssociation> LoadAssociationsGuarded()
        {
            try { return _settingsService.LoadAssociations(); }
            catch (InvalidDataException ex) { BlockSettingsPersistence(ex); return new List<RoiAssociation>(); }
        }

        private void SaveSettingsGuarded()
        {
            if (_settingsPersistenceBlocked)
            {
                AppendLog("Settings not saved — an unreadable settings file is being preserved.");
                return;
            }
            _settingsService.SaveSettings(_settings);
        }

        private void BlockSettingsPersistence(InvalidDataException ex)
        {
            _settingsPersistenceBlocked = true;
            StatusText = ex.Message;
            AppendLog(ex.Message);
        }

        private bool _settingsNotSavedLogged;

        /// <summary>
        /// Writes one changed export toggle to settings.json straight away. The file is re-read
        /// first and only this field is changed on it: a second forward window may have saved its
        /// own toggles since this one loaded, and writing this window's whole snapshot back would
        /// revert them. While an unreadable settings file is being preserved the change stays in
        /// memory, and the log says so once.
        /// </summary>
        private void PersistSetting(Action<AppSettings> change)
        {
            change(_settings);
            if (_settingsPersistenceBlocked)
            {
                if (!_settingsNotSavedLogged)
                {
                    _settingsNotSavedLogged = true;
                    AppendLog("Settings not saved — an unreadable settings file is being preserved.");
                }
                return;
            }

            try
            {
                AppSettings onDisk = _settingsService.LoadSettings();
                change(onDisk);
                _settingsService.SaveSettings(onDisk);
                _settings = onDisk;
            }
            catch (InvalidDataException ex)
            {
                BlockSettingsPersistence(ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AppendLog($"Settings not saved: {ex.Message}");
            }
        }

        /// <summary>
        /// Records a folder in gui_state.json once it names a directory that exists, so a path
        /// being typed is not saved one keystroke at a time.
        /// </summary>
        private void RememberFolder(string folder, Action<GuiState> change)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            string error;
            if (!_guiState.Update(change, out error))
                NoteGuiStateNotSaved(error);
        }

        /// <summary>Records one changed series-selection control in gui_state.json straight away.</summary>
        private void PersistGuiState(Action<GuiState> change)
        {
            string error;
            if (!_guiState.Update(change, out error))
                NoteGuiStateNotSaved(error);
        }

        private void NoteGuiStateNotSaved(string reason)
        {
            if (_guiStateErrorLogged)
                return;
            _guiStateErrorLogged = true;
            AppendLog("Last-used folders and series filters will not be remembered: " + reason);
        }

        /// <summary>
        /// The anonymization key for an export to <see cref="OutputFolder"/>, at its location
        /// outside the export (<see cref="AnonymizationKeyLocation"/>). A key an earlier export left
        /// inside the folder is merged in, and the log says where that copy still sits.
        /// </summary>
        /// <exception cref="ArgumentException">The key location is inside the export.</exception>
        private AnonymizationService OpenAnonymizationKey()
        {
            string legacyKeyWarning;
            var service = AnonymizationKeyLocation.Open(
                OutputFolder, AnonymizationKeyPath, _settings.HashSalt, out legacyKeyWarning);
            if (legacyKeyWarning != null)
                AppendLog("  Warning: " + legacyKeyWarning);
            return service;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
