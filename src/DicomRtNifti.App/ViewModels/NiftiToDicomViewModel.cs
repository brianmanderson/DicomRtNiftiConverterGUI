using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.App.Services;
using DicomRtNifti.App.Views;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// ViewModel for the "NIfTI to DICOM" window. Pointed at a folder, it scans (recursively,
    /// to any depth) for DICOM folders containing a "masks/"/"doses/" subdirectory or an
    /// image.nii.gz and converts each in batch.
    ///
    /// Ported from WPF, including the always-on server (file-watcher) mode that periodically
    /// re-scans the root and converts folders once their contents have settled. Commands use
    /// CommunityToolkit.Mvvm (Avalonia has no WPF CommandManager auto-requery, so
    /// RefreshCommands() raises CanExecuteChanged explicitly). The server timer uses Avalonia's
    /// DispatcherTimer in place of WPF's.
    ///
    /// Parity with the forward window: a timestamped log and a progress bar; the folder walk runs
    /// off the UI thread; every conversion, server-mode ones included, runs under a cancellation
    /// token that Stop cancels; IsBusy is raised while a server tick converts, so Stop is live
    /// then too. A job Stop cancels in server mode is not retried until its files change.
    /// Server mode rebuilds an existing RT-STRUCT (or RT-DOSE) when an input is newer than it
    /// (decision D-12), so a mask overwritten in place is no longer silently ignored.
    /// </summary>
    public class NiftiToDicomViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly RtStructWriterService _rtStructWriter;
        private readonly RtDoseWriterService _rtDoseWriter;
        private readonly NiftiMetadataService _metadataService;
        private readonly NiftiImageWriterService _imageWriter;
        private readonly IFolderPicker _folderPicker;
        private readonly GuiStateStore _guiState;
        private bool _guiStateErrorLogged;

        private string _rootFolder = "";
        private string _statusText = "Browse to a folder. Each DICOM folder containing a 'masks/' or 'doses/' subdirectory (or an image.nii.gz) will be converted.";
        private string _logText = "";
        private double _progressValue;
        private bool _isBusy;
        private bool _convertStructures = true;
        private bool _convertDoses = true;
        private bool _convertImage = true;
        private bool _isServerMode;
        private CancellationTokenSource _cts;

        // Server-mode state
        private DispatcherTimer _serverTimer;
        private bool _tickInFlight;
        private Task _serverTick = Task.CompletedTask;
        private Dictionary<string, string> _fingerprints
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Folder -> its signature when Stop cancelled its job. Skipped until the signature moves.
        private readonly Dictionary<string, string> _cancelledFingerprints
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public NiftiToDicomViewModel(
            DicomScannerService scannerService,
            RtStructWriterService rtStructWriter,
            RtDoseWriterService rtDoseWriter,
            NiftiMetadataService metadataService,
            NiftiImageWriterService imageWriter,
            IFolderPicker folderPicker,
            GuiStateStore guiState = null)
        {
            _scannerService = scannerService;
            _rtStructWriter = rtStructWriter;
            _rtDoseWriter = rtDoseWriter;
            _metadataService = metadataService;
            _imageWriter = imageWriter;
            _folderPicker = folderPicker;
            _guiState = guiState ?? new GuiStateStore();

            DiscoveredJobs = new ObservableCollection<NiftiToDicomJob>();
            DiscoveredJobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowJobsEmptyHint));

            BrowseRootFolderCommand = new AsyncRelayCommand(BrowseRootFolderAsync, () => !IsBusy && !IsServerMode);
            ScanRootFolderCommand = new AsyncRelayCommand(ScanAsync,
                () => !IsBusy && !IsServerMode && !string.IsNullOrEmpty(RootFolder));
            ConvertCommand = new AsyncRelayCommand(ConvertAllAsync,
                () => !IsBusy && !IsServerMode && DiscoveredJobs.Count > 0 && (ConvertStructures || ConvertDoses || ConvertImage));
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            // Stop Server stays available while a server job runs; stopping cancels that job.
            RunServerCommand = new RelayCommand(ToggleServer,
                () => IsServerMode || (!IsBusy && (ConvertStructures || ConvertDoses || ConvertImage) && !string.IsNullOrEmpty(RootFolder)));
            OpenHelpCommand = new RelayCommand(OpenHelp);

            // Restore the last folder and the three toggles. Backing fields, so nothing is written back.
            var gui = _guiState.Load();
            if (_guiState.IsBlocked)
                NoteGuiStateNotSaved(_guiState.BlockedReason);
            if (Directory.Exists(gui.LastReverseFolder ?? ""))
                _rootFolder = gui.LastReverseFolder;
            _convertImage = gui.ReverseConvertImage;
            _convertStructures = gui.ReverseConvertStructures;
            _convertDoses = gui.ReverseConvertDoses;
        }

        private void OpenHelp()
        {
            var help = new NiftiToDicomHelpWindow();
            var owner = AppWindows.Active;
            if (owner != null)
                help.Show(owner);
            else
                help.Show();
        }

        public string RootFolder
        {
            get { return _rootFolder; }
            set
            {
                _rootFolder = value;
                OnPropertyChanged();
                RefreshCommands();
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    PersistGuiState(s => s.LastReverseFolder = value);
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(); }
        }

        /// <summary>Timestamped run log, as in the forward window: scans, per-job outcomes, full error text.</summary>
        public string LogText
        {
            get { return _logText; }
            private set { _logText = value; OnPropertyChanged(); }
        }

        /// <summary>0-100 over the jobs of the current Convert All, or of the current server tick.</summary>
        public double ProgressValue
        {
            get { return _progressValue; }
            private set { _progressValue = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set { _isBusy = value; OnPropertyChanged(); RefreshCommands(); }
        }

        /// <summary>When true, masks/*.nii.gz files are converted to RT-STRUCT.</summary>
        public bool ConvertStructures
        {
            get { return _convertStructures; }
            set
            {
                if (_convertStructures == value) return;
                _convertStructures = value; OnPropertyChanged(); RefreshCommands();
                PersistGuiState(s => s.ReverseConvertStructures = value);
            }
        }

        /// <summary>When true, doses/*.nii.gz files are converted to RT-DOSE.</summary>
        public bool ConvertDoses
        {
            get { return _convertDoses; }
            set
            {
                if (_convertDoses == value) return;
                _convertDoses = value; OnPropertyChanged(); RefreshCommands();
                PersistGuiState(s => s.ReverseConvertDoses = value);
            }
        }

        /// <summary>When true, an image.nii.gz file is converted to a DICOM image series first.</summary>
        public bool ConvertImage
        {
            get { return _convertImage; }
            set
            {
                if (_convertImage == value) return;
                _convertImage = value; OnPropertyChanged(); RefreshCommands();
                PersistGuiState(s => s.ReverseConvertImage = value);
            }
        }

        /// <summary>True while the periodic watcher is active.</summary>
        public bool IsServerMode
        {
            get { return _isServerMode; }
            private set
            {
                _isServerMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ServerButtonText));
                RefreshCommands();
            }
        }

        /// <summary>"Run Server" / "Stop Server" — bound to the server toggle button.</summary>
        public string ServerButtonText => IsServerMode ? "Stop Server" : "Run Server";

        /// <summary>One row per DICOM folder eligible for conversion.</summary>
        public ObservableCollection<NiftiToDicomJob> DiscoveredJobs { get; }

        /// <summary>True when no jobs have been discovered yet — drives the grid's empty-state hint.</summary>
        public bool ShowJobsEmptyHint => DiscoveredJobs.Count == 0;

        public IAsyncRelayCommand BrowseRootFolderCommand { get; }
        public IAsyncRelayCommand ScanRootFolderCommand { get; }
        public IAsyncRelayCommand ConvertCommand { get; }
        public IRelayCommand CancelCommand { get; }
        public IRelayCommand RunServerCommand { get; }
        public IRelayCommand OpenHelpCommand { get; }

        /// <summary>How often server mode re-scans. Ten seconds; tests shorten or drive ticks directly.</summary>
        internal TimeSpan ServerInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The server tick in flight, or the last one to run. Tests await it.</summary>
        internal Task LastServerTick => _serverTick;

        /// <summary>Runs one server tick now (or returns the one in flight), as the timer would.</summary>
        internal Task RunServerTickAsync()
        {
            if (!_tickInFlight)
                _serverTick = OnServerTickAsync();
            return _serverTick;
        }

        private void RefreshCommands()
        {
            BrowseRootFolderCommand.NotifyCanExecuteChanged();
            ScanRootFolderCommand.NotifyCanExecuteChanged();
            ConvertCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            RunServerCommand.NotifyCanExecuteChanged();
        }

        // ------- private helpers -------

        private async Task BrowseRootFolderAsync()
        {
            string picked = await _folderPicker.PickFolderAsync(
                "Select a DICOM folder (or parent folder containing several DICOM folders)", RootFolder);
            if (string.IsNullOrEmpty(picked)) return;

            RootFolder = picked;
            // Folder selection no longer auto-scans — the user clicks the separate Scan button.
            StatusText = "Folder set. Click 'Scan' to discover convertible folders.";
        }

        /// <summary>
        /// Populates DiscoveredJobs by checking the root folder and every descendant subfolder
        /// (to any depth) for a "masks/" and/or "doses/" subdirectory (or an image.nii.gz).
        /// Each match is a job. The walk runs on the thread pool, so a large tree does not freeze
        /// the window, and Stop abandons it.
        /// </summary>
        private async Task ScanAsync()
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            ProgressValue = 0;
            try
            {
                AppendLog($"Scanning {RootFolder}...");
                StatusText = "Scanning...";
                string root = RootFolder;
                var token = _cts.Token;
                var found = await Task.Run(() => FindJobs(root, token), token).ConfigureAwait(true);
                ApplyDiscovered(found, keepStatuses: false);

                if (DiscoveredJobs.Count == 0)
                {
                    StatusText = "No folders with a 'masks/' or 'doses/' subdirectory or 'image.nii.gz' were found.";
                }
                else
                {
                    int totalMasks = DiscoveredJobs.Sum(j => j.MaskCount);
                    int totalDoses = DiscoveredJobs.Sum(j => j.DoseCount);
                    int totalImages = DiscoveredJobs.Count(j => j.HasImage);
                    StatusText = $"Found {DiscoveredJobs.Count} folder(s): {totalImages} image(s), {totalMasks} mask(s), {totalDoses} dose(s) total. Click Convert to proceed.";
                }
                AppendLog(StatusText);
                ProgressValue = 100;
            }
            catch (OperationCanceledException)
            {
                StatusText = "Scan cancelled.";
                AppendLog(StatusText);
            }
            catch (DirectoryNotFoundException)
            {
                DiscoveredJobs.Clear();
                StatusText = "Folder does not exist.";
                AppendLog($"{StatusText} ({RootFolder})");
            }
            catch (Exception ex)
            {
                StatusText = $"Error scanning subfolders: {DescribeError(ex)}";
                AppendLog(StatusText);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        /// <summary>What one discovered folder holds; built off the UI thread.</summary>
        private sealed class JobSpec
        {
            public string Folder;
            public List<string> MaskNames;
            public List<string> DoseNames;
            public bool HasImage;
        }

        private static List<JobSpec> FindJobs(string root, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException($"Folder does not exist: '{root}'.");

            var jobs = new List<JobSpec>();
            foreach (var folder in EnumerateCandidateFolders(root))
            {
                ct.ThrowIfCancellationRequested();
                var maskFiles = GetMaskFiles(folder);
                var doseFiles = GetDoseFiles(folder);
                bool hasImage = HasImageNifti(folder);
                if (maskFiles.Count == 0 && doseFiles.Count == 0 && !hasImage)
                    continue;

                jobs.Add(new JobSpec
                {
                    Folder = folder,
                    MaskNames = maskFiles.Select(StripNiiGz).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    DoseNames = doseFiles.Select(StripNiiGz).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    HasImage = hasImage,
                });
            }
            return jobs;
        }

        /// <summary>
        /// Replaces the grid with <paramref name="found"/>. A manual scan starts every row at
        /// Pending; a server tick keeps each surviving row (and its status) and only adds and
        /// removes folders, so the grid does not flicker back to Pending every ten seconds.
        /// </summary>
        private void ApplyDiscovered(List<JobSpec> found, bool keepStatuses)
        {
            if (!keepStatuses)
            {
                DiscoveredJobs.Clear();
                foreach (var spec in found)
                    DiscoveredJobs.Add(NewJob(spec));
                return;
            }

            var existing = new Dictionary<string, NiftiToDicomJob>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in DiscoveredJobs)
                existing.TryAdd(job.DicomFolder, job);

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in found)
            {
                present.Add(spec.Folder);
                NiftiToDicomJob job;
                if (existing.TryGetValue(spec.Folder, out job))
                {
                    job.SetContents(spec.MaskNames, spec.DoseNames, spec.HasImage);
                }
                else
                {
                    DiscoveredJobs.Add(NewJob(spec));
                    AppendLog($"Server: found {Path.GetFileName(spec.Folder)} ({Describe(spec)}).");
                }
            }

            for (int i = DiscoveredJobs.Count - 1; i >= 0; i--)
            {
                if (!present.Contains(DiscoveredJobs[i].DicomFolder))
                    DiscoveredJobs.RemoveAt(i);
            }
        }

        private static string Describe(JobSpec spec)
        {
            var parts = new List<string>();
            if (spec.HasImage) parts.Add("image");
            if (spec.MaskNames.Count > 0) parts.Add($"{spec.MaskNames.Count} mask(s)");
            if (spec.DoseNames.Count > 0) parts.Add($"{spec.DoseNames.Count} dose(s)");
            return string.Join(", ", parts);
        }

        private static NiftiToDicomJob NewJob(JobSpec spec)
        {
            var job = new NiftiToDicomJob
            {
                DicomFolder = spec.Folder,
                FolderDisplayName = Path.GetFileName(spec.Folder),
                OutputPath = Path.Combine(spec.Folder, $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm"),
                Status = "Pending"
            };
            job.SetContents(spec.MaskNames, spec.DoseNames, spec.HasImage);
            return job;
        }

        /// <summary>
        /// Yields the root folder and every descendant directory, recursing to any depth,
        /// but never descending into (or yielding) a "masks/" or "doses/" subtree — those
        /// hold the NIfTI inputs, not convertible DICOM folders. Unreadable directories are
        /// skipped silently rather than aborting the whole scan.
        /// </summary>
        private static IEnumerable<string> EnumerateCandidateFolders(string root)
        {
            yield return root;

            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                List<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(current).ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var sub in children)
                {
                    string leaf = Path.GetFileName(sub);
                    if (string.Equals(leaf, "masks", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(leaf, "doses", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return sub;
                    stack.Push(sub);
                }
            }
        }

        private static List<string> GetMaskFiles(string folder)
            => NiftiFileNaming.EnumerateNiftiFiles(Path.Combine(folder, "masks")).ToList();

        private static List<string> GetDoseFiles(string folder)
            => NiftiFileNaming.EnumerateNiftiFiles(Path.Combine(folder, "doses")).ToList();

        private static bool HasImageNifti(string folder)
            => NiftiFileNaming.TryGetImageNiftiPath(folder, out _);

        private static string StripNiiGz(string fileName) => NiftiFileNaming.StripNiftiExtension(fileName);

        private async Task ConvertAllAsync()
        {
            if (DiscoveredJobs.Count == 0) return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            int success = 0, fail = 0, done = 0;
            var jobs = DiscoveredJobs.ToList();
            ProgressValue = 0;
            AppendLog($"Converting {jobs.Count} folder(s)...");
            NiftiToDicomJob current = null;

            try
            {
                foreach (var job in jobs)
                {
                    ct.ThrowIfCancellationRequested();
                    current = job;

                    string folder = job.DicomFolder;
                    var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                    var summary = new List<string>();
                    bool jobFailed = false;
                    bool didAnything = false;

                    NiftiPatientMetadata metadata;
                    try
                    {
                        metadata = await Task.Run(() => _metadataService.LoadOrSynthesize(folder), ct).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        FailJob(job, "metadata.json: " + DescribeError(ex));
                        fail++;
                        ProgressValue = 100.0 * ++done / jobs.Count;
                        continue;
                    }

                    if (job.HasImage && ConvertImage)
                    {
                        try
                        {
                            job.Status = "Converting image...";
                            StatusText = $"{job.FolderDisplayName}: writing image series...";
                            var imageWritten = await Task.Run(() =>
                                _imageWriter.ConvertImageNiftiToDicomSeries(
                                    folder, metadata, progress, ct)).ConfigureAwait(true);
                            if (imageWritten.Count > 0)
                            {
                                summary.Add($"image series ({imageWritten.Count} slices)");
                                didAnything = true;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"image FAILED: {DescribeError(ex)}");
                            jobFailed = true;
                        }
                    }
                    else if (job.HasImage && !ConvertImage)
                    {
                        summary.Add("image skipped");
                    }

                    ct.ThrowIfCancellationRequested();
                    job.Status = "Scanning DICOM...";
                    StatusText = $"Processing {job.FolderDisplayName}: scanning DICOM...";

                    DicomSeriesGroup refSeries = null;
                    try
                    {
                        refSeries = await Task.Run(async () =>
                            await PickReferenceSeriesAsync(folder, ct).ConfigureAwait(false))
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        FailJob(job, "scan: " + DescribeError(ex));
                        fail++;
                        ProgressValue = 100.0 * ++done / jobs.Count;
                        continue;
                    }

                    if (refSeries == null && (job.MaskCount > 0 || job.DoseCount > 0))
                    {
                        StatusText = $"{job.FolderDisplayName}: no reference DICOM — using metadata.json fallback.";
                    }

                    if (job.MaskCount > 0 && ConvertStructures)
                    {
                        ct.ThrowIfCancellationRequested();
                        didAnything = true;
                        try
                        {
                            job.Status = "Converting masks...";
                            StatusText = $"{job.FolderDisplayName}: building RT-STRUCT...";
                            string outPath = job.OutputPath;
                            string written = await Task.Run(() =>
                                _rtStructWriter.ConvertMasksFolderToRtStruct(
                                    folder, refSeries, outPath, progress, ct, metadata)).ConfigureAwait(true);
                            summary.Add($"1 RT-STRUCT ({Path.GetFileName(written)})");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"masks FAILED: {DescribeError(ex)}");
                            jobFailed = true;
                        }
                    }
                    else if (job.MaskCount > 0 && !ConvertStructures)
                    {
                        summary.Add("masks skipped");
                    }

                    if (job.DoseCount > 0 && ConvertDoses)
                    {
                        ct.ThrowIfCancellationRequested();
                        didAnything = true;
                        try
                        {
                            job.Status = "Converting doses...";
                            StatusText = $"{job.FolderDisplayName}: building RT-DOSE files...";
                            var written = await Task.Run(() =>
                                _rtDoseWriter.ConvertDoseFolderToRtDoses(
                                    folder, refSeries, progress, ct,
                                    useStableHashNames: false, skipIfExists: false,
                                    metadata: metadata)).ConfigureAwait(true);
                            summary.Add($"{written.Count} RT-DOSE");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"doses FAILED: {DescribeError(ex)}");
                            jobFailed = true;
                        }
                    }
                    else if (job.DoseCount > 0 && !ConvertDoses)
                    {
                        summary.Add("doses skipped");
                    }

                    if (!didAnything)
                    {
                        job.Status = summary.Count > 0
                            ? "Skipped — " + string.Join(", ", summary)
                            : "Skipped — nothing to convert.";
                        AppendLog($"{job.FolderDisplayName}: {job.Status}");
                    }
                    else if (jobFailed)
                    {
                        FailJob(job, string.Join("; ", summary));
                        fail++;
                    }
                    else
                    {
                        job.Status = "OK — " + string.Join(", ", summary);
                        AppendLog($"{job.FolderDisplayName}: {job.Status}");
                        success++;
                    }
                    current = null;
                    ProgressValue = 100.0 * ++done / jobs.Count;
                }

                StatusText = $"Done. {success} succeeded, {fail} failed.";
                AppendLog(StatusText);
            }
            catch (OperationCanceledException)
            {
                if (current != null)
                    current.Status = "Cancelled.";
                StatusText = "Conversion cancelled.";
                AppendLog($"{StatusText} {success} succeeded, {fail} failed before Stop.");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        /// <summary>Scans a single DICOM folder and returns the first CT/MR/PT image series, or null.</summary>
        private async Task<DicomSeriesGroup> PickReferenceSeriesAsync(string dicomFolder, CancellationToken ct)
        {
            var scanResult = await _scannerService.ScanFolderAsync(dicomFolder, null, ct).ConfigureAwait(false);
            foreach (var patient in scanResult.Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.Series)
                        if (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT")
                            return series;
            return null;
        }

        /// <summary>
        /// Marks a job failed with the full error text. The grid's Status cell is trimmed to its
        /// column, so the whole message also goes to the log, where it can be read and copied.
        /// </summary>
        private void FailJob(NiftiToDicomJob job, string detail)
        {
            job.Status = "FAILED — " + detail;
            AppendLog($"{job.FolderDisplayName}: {job.Status}");
        }

        /// <summary>
        /// An exception's message followed by those of its inner exceptions, so a failure that a
        /// writer wraps (a SimpleITK or fo-dicom error inside an InvalidOperationException, say)
        /// reaches the user whole instead of as its outermost sentence.
        /// </summary>
        internal static string DescribeError(Exception ex)
        {
            if (ex == null)
                return "";
            var messages = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                // An AggregateException's own message only restates its inner ones.
                if (e is AggregateException agg)
                {
                    if (agg.InnerExceptions.Count > 1)
                    {
                        messages.AddRange(agg.InnerExceptions.Select(DescribeError));
                        break;
                    }
                    continue;
                }
                string message = (e.Message ?? "").Trim();
                if (message.Length > 0 && !messages.Contains(message))
                    messages.Add(message);
            }
            return messages.Count == 0 ? ex.GetType().Name : string.Join(" → ", messages);
        }

        // ------- server (watcher) mode -------

        private void ToggleServer()
        {
            if (IsServerMode) StopServer();
            else StartServer();
        }

        private void StartServer()
        {
            if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            {
                StatusText = "Cannot start server: root folder not set.";
                return;
            }

            _fingerprints.Clear();
            _cancelledFingerprints.Clear();
            IsServerMode = true;
            _serverTimer = new DispatcherTimer { Interval = ServerInterval };
            _serverTimer.Tick += (s, e) => RunServerTickAsync();
            _serverTimer.Start();

            StatusText = $"Server running — watching '{RootFolder}' every {ServerInterval.TotalSeconds:0.#}s.";
            AppendLog(StatusText);
            PersistGuiState(s => s.LastReverseFolder = RootFolder);
            // Snapshot baseline immediately so the next tick can compare.
            RunServerTickAsync();
        }

        private void StopServer()
        {
            _serverTimer?.Stop();
            _serverTimer = null;
            _fingerprints.Clear();
            _cancelledFingerprints.Clear();
            IsServerMode = false;
            // A job in flight is cancelled with the server; its tick reports the cancellation.
            _cts?.Cancel();
            StatusText = "Server stopped.";
            AppendLog(StatusText);
        }

        /// <summary>
        /// One watcher pass: re-discover the jobs (off the UI thread), fingerprint each folder,
        /// and convert the folders whose fingerprint has not moved since the previous pass. Never
        /// throws: it runs from a timer.
        /// </summary>
        private async Task OnServerTickAsync()
        {
            if (_tickInFlight || !IsServerMode) return;
            _tickInFlight = true;
            try
            {
                // 1. Re-discover jobs from the current root, and fingerprint them, off the UI thread.
                string root = RootFolder;
                var (found, signatures) = await Task.Run(() =>
                {
                    var jobs = FindJobs(root, CancellationToken.None);
                    var sigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var spec in jobs)
                        sigs[spec.Folder] = ComputeFolderSignature(spec.Folder);
                    return (jobs, sigs);
                }).ConfigureAwait(true);

                if (!IsServerMode) return; // stopped while the walk ran
                ApplyDiscovered(found, keepStatuses: true);
                if (DiscoveredJobs.Count == 0)
                {
                    StatusText = $"Server running — no convertible folders under '{RootFolder}'.";
                    return;
                }

                // 2. Decide which folders are settled (same signature as last tick). A folder whose
                //    job Stop cancelled waits until its files change.
                var settled = new List<NiftiToDicomJob>();
                int parked = 0;
                foreach (var job in DiscoveredJobs)
                {
                    string sig;
                    if (!signatures.TryGetValue(job.DicomFolder, out sig))
                        continue;

                    string cancelledAt;
                    if (_cancelledFingerprints.TryGetValue(job.DicomFolder, out cancelledAt))
                    {
                        if (cancelledAt == sig)
                        {
                            parked++;
                            continue;
                        }
                        _cancelledFingerprints.Remove(job.DicomFolder);
                    }

                    string prev;
                    if (_fingerprints.TryGetValue(job.DicomFolder, out prev) && prev == sig)
                        settled.Add(job);
                    else
                        job.Status = "Watching — files changed, waiting for stability.";
                }

                // 3. Persist fingerprints for next tick (drops entries for folders that disappeared).
                _fingerprints = signatures;

                if (settled.Count == 0)
                {
                    int waiting = DiscoveredJobs.Count - parked;
                    var parts = new List<string>();
                    if (waiting > 0) parts.Add($"{waiting} folder(s) waiting for stability");
                    if (parked > 0) parts.Add($"{parked} cancelled, left alone until their files change");
                    StatusText = $"Server running — {string.Join("; ", parts)}. Watching '{RootFolder}'.";
                    return;
                }

                // 4. Run conversions for settled folders, in sequence, under a token Stop cancels.
                StatusText = $"Server running — {settled.Count} settled folder(s), running...";
                int converted = 0, upToDate = 0, failed = 0, done = 0;
                bool cancelled = false;
                IsBusy = true;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                ProgressValue = 0;
                try
                {
                    foreach (var job in settled)
                    {
                        ct.ThrowIfCancellationRequested();
                        var outcome = await RunSettledJobAsync(job, ct).ConfigureAwait(true);
                        if (outcome == JobOutcome.Converted) converted++;
                        else if (outcome == JobOutcome.UpToDate) upToDate++;
                        else if (outcome == JobOutcome.Failed) failed++;
                        ProgressValue = 100.0 * ++done / settled.Count;
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    IsBusy = false;
                }

                if (!IsServerMode)
                    return; // Stop Server: StopServer already reported.

                if (cancelled)
                {
                    StatusText = $"Server running — Stop cancelled the job in progress; it is not retried until its files change. Watching '{RootFolder}'.";
                    AppendLog("Server: Stop cancelled the job in progress.");
                }
                else
                {
                    StatusText = $"Server running — last tick: {converted} converted, {upToDate} up-to-date, {failed} failed. Watching '{RootFolder}'.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Server tick error: {DescribeError(ex)}";
                AppendLog(StatusText);
            }
            finally
            {
                _tickInFlight = false;
            }
        }

        /// <summary>
        /// Composite signature combining file count, total size and max LastWriteTimeUtc
        /// for the DICOM folder + masks/ + doses/ subdirs. Two consecutive identical
        /// signatures = the folder is "settled" for conversion.
        /// </summary>
        private static string ComputeFolderSignature(string dicomFolder)
        {
            string sigDicom = SignatureForDir(dicomFolder, "*", SearchOption.TopDirectoryOnly);
            // Use "*.nii*" so both .nii and .nii.gz contribute to the signature; otherwise a
            // user dropping in a plain .nii would never trigger the "folder is settled" check.
            string sigMasks = SignatureForDir(Path.Combine(dicomFolder, "masks"), "*.nii*", SearchOption.TopDirectoryOnly);
            string sigDoses = SignatureForDir(Path.Combine(dicomFolder, "doses"), "*.nii*", SearchOption.TopDirectoryOnly);
            return $"D[{sigDicom}]M[{sigMasks}]X[{sigDoses}]";
        }

        private static string SignatureForDir(string dir, string pattern, SearchOption opt)
        {
            if (!Directory.Exists(dir)) return "0:0:0";
            int count = 0;
            long maxTicks = 0;
            long totalBytes = 0;
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, pattern, opt))
                {
                    var fi = new FileInfo(path);
                    count++;
                    totalBytes += fi.Length;
                    long t = fi.LastWriteTimeUtc.Ticks;
                    if (t > maxTicks) maxTicks = t;
                }
            }
            catch (Exception)
            {
                return "err";
            }
            return $"{count}:{maxTicks}:{totalBytes}";
        }

        /// <summary>Where a server-mode output stands against the inputs it was built from.</summary>
        internal enum OutputState { Missing, Stale, UpToDate }

        /// <summary>
        /// Decision D-12: an output is stale when any of its inputs was written after it. Server
        /// outputs are named from the input file names alone, so without this a mask overwritten
        /// in place (a corrected prediction under the same name) kept its old RT-STRUCT forever.
        /// </summary>
        internal static OutputState CompareToOutput(IEnumerable<string> inputs, string outputPath)
        {
            if (!File.Exists(outputPath))
                return OutputState.Missing;
            DateTime built = File.GetLastWriteTimeUtc(outputPath);
            foreach (var input in inputs)
            {
                if (File.Exists(input) && File.GetLastWriteTimeUtc(input) > built)
                    return OutputState.Stale;
            }
            return OutputState.UpToDate;
        }

        private async Task<JobOutcome> RunSettledJobAsync(NiftiToDicomJob job, CancellationToken ct)
        {
            string folder = job.DicomFolder;
            var summary = new List<string>();
            bool jobFailed = false;
            bool didAnything = false;
            IProgress<string> progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");

            try
            {
                // Step 0: load metadata.json (creating one if needed). The same UIDs persist
                // across the image / mask / dose passes.
                job.Status = "Preparing...";
                NiftiPatientMetadata metadata;
                try
                {
                    metadata = await Task.Run(() => _metadataService.LoadOrSynthesize(folder), ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    FailJob(job, "metadata.json: " + DescribeError(ex));
                    return JobOutcome.Failed;
                }

                // Step 1: convert image.nii.gz once. Subsequent ticks skip if the series UID
                // recorded in metadata.json is already on disk.
                if (job.HasImage && ConvertImage)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        job.Status = "Converting image...";
                        var imageWritten = await Task.Run(() =>
                            _imageWriter.ConvertImageNiftiToDicomSeries(folder, metadata, progress, ct))
                            .ConfigureAwait(true);
                        if (imageWritten.Count > 0)
                        {
                            summary.Add($"image series ({imageWritten.Count} slices)");
                            didAnything = true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        summary.Add($"image FAILED: {DescribeError(ex)}");
                        jobFailed = true;
                    }
                }

                // Step 2: scan for a reference image series — possibly the one we just wrote.
                ct.ThrowIfCancellationRequested();
                job.Status = "Scanning DICOM...";
                DicomSeriesGroup refSeries;
                try
                {
                    refSeries = await Task.Run(async () =>
                        await PickReferenceSeriesAsync(folder, ct).ConfigureAwait(false))
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    FailJob(job, "scan: " + DescribeError(ex));
                    return JobOutcome.Failed;
                }

                // refSeries may be null if there's no image and no DICOMs — the writers handle
                // that case via the metadata fallback.

                // RT-STRUCT: named from the sorted mask basenames; rebuilt when missing or when a
                // mask is newer than it (D-12), otherwise left alone.
                if (job.MaskCount > 0 && ConvertStructures)
                {
                    string structFileName = HashNaming.RtStructFileName(job.MaskBaseNames);
                    string structOutPath = Path.Combine(folder, structFileName);
                    var state = CompareToOutput(GetMaskFiles(folder), structOutPath);

                    if (state == OutputState.UpToDate)
                    {
                        summary.Add($"masks up-to-date ({structFileName})");
                    }
                    else
                    {
                        ct.ThrowIfCancellationRequested();
                        didAnything = true;
                        try
                        {
                            job.Status = state == OutputState.Stale
                                ? "Rebuilding RT-STRUCT (a mask is newer)..."
                                : "Converting masks...";
                            string written = await Task.Run(() =>
                                _rtStructWriter.ConvertMasksFolderToRtStruct(
                                    folder, refSeries, structOutPath, progress, ct, metadata))
                                .ConfigureAwait(true);
                            summary.Add(state == OutputState.Stale
                                ? $"RT-STRUCT rebuilt, a mask was newer ({Path.GetFileName(written)})"
                                : $"RT-STRUCT ({Path.GetFileName(written)})");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"masks FAILED: {DescribeError(ex)}");
                            jobFailed = true;
                        }
                    }
                }

                // RT-DOSE: per-file hash filename; skip-if-exists handled inside the writer, which
                // is switched off when any dose is newer than its existing RT-DOSE (D-12).
                if (job.DoseCount > 0 && ConvertDoses)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        job.Status = "Converting doses...";
                        bool rebuild = GetDoseFiles(folder).Any(dose => CompareToOutput(
                            new[] { dose },
                            Path.Combine(folder, HashNaming.RtDoseFileName(StripNiiGz(dose)))) == OutputState.Stale);

                        var beforeCount = Directory
                            .EnumerateFiles(folder, "RTDOSE_*.dcm", SearchOption.TopDirectoryOnly)
                            .Count();

                        var written = await Task.Run(() =>
                            _rtDoseWriter.ConvertDoseFolderToRtDoses(
                                folder, refSeries, progress, ct,
                                useStableHashNames: true, skipIfExists: !rebuild,
                                metadata: metadata))
                            .ConfigureAwait(true);

                        var afterCount = Directory
                            .EnumerateFiles(folder, "RTDOSE_*.dcm", SearchOption.TopDirectoryOnly)
                            .Count();
                        int newDoses = afterCount - beforeCount;

                        if (rebuild)
                        {
                            didAnything = true;
                            summary.Add($"{written.Count} RT-DOSE rebuilt, a dose was newer");
                        }
                        else
                        {
                            if (newDoses > 0) didAnything = true;
                            summary.Add(newDoses > 0
                                ? $"{newDoses} new RT-DOSE"
                                : $"doses up-to-date ({written.Count})");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        summary.Add($"doses FAILED: {DescribeError(ex)}");
                        jobFailed = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop (or Stop Server). Remember the folder as it is now — the job may have
                // written metadata.json or image slices — so the next tick does not just rerun it.
                if (IsServerMode)
                {
                    _cancelledFingerprints[folder] = ComputeFolderSignature(folder);
                    job.Status = "Cancelled — not retried until its files change.";
                }
                else
                {
                    job.Status = "Cancelled — server stopped.";
                }
                AppendLog($"{job.FolderDisplayName}: {job.Status}");
                throw;
            }

            if (jobFailed)
            {
                FailJob(job, string.Join("; ", summary));
                return JobOutcome.Failed;
            }
            if (!didAnything)
            {
                job.Status = summary.Count > 0
                    ? "Up-to-date — " + string.Join(", ", summary)
                    : "Up-to-date — nothing to do.";
                return JobOutcome.UpToDate;
            }
            job.Status = "Converted — " + string.Join(", ", summary);
            AppendLog($"{job.FolderDisplayName}: {job.Status}");
            return JobOutcome.Converted;
        }

        private enum JobOutcome { Converted, UpToDate, Failed }

        private void Cancel() => _cts?.Cancel();

        /// <summary>
        /// Called when the host window closes: stops the watcher timer so it doesn't keep
        /// ticking after the window is gone, and cancels any in-flight conversion.
        /// </summary>
        public void OnWindowClosed()
        {
            if (IsServerMode) StopServer();
            _cts?.Cancel();
        }

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogText += $"[{timestamp}] {message}\n";
        }

        /// <summary>Writes one changed field to gui_state.json; a failure is logged once.</summary>
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
            AppendLog("The folder and Convert toggles will not be remembered: " + reason);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One row in the batch list: a DICOM folder + its discovered masks/doses + status.</summary>
    public class NiftiToDicomJob : INotifyPropertyChanged
    {
        private string _status = "Pending";
        private string _maskNames = "";
        private int _maskCount;
        private string _doseNames = "";
        private int _doseCount;
        private bool _hasImage;

        public string DicomFolder { get; set; } = "";
        public string FolderDisplayName { get; set; } = "";
        public string OutputPath { get; set; } = "";

        /// <summary>Mask file names without extension, sorted: what server mode hashes into the RT-STRUCT name.</summary>
        public IReadOnlyList<string> MaskBaseNames { get; private set; } = new string[0];

        public string MaskNames { get { return _maskNames; } set { _maskNames = value; OnPropertyChanged(); } }
        public int MaskCount { get { return _maskCount; } set { _maskCount = value; OnPropertyChanged(); } }
        public string DoseNames { get { return _doseNames; } set { _doseNames = value; OnPropertyChanged(); } }
        public int DoseCount { get { return _doseCount; } set { _doseCount = value; OnPropertyChanged(); } }

        public bool HasImage
        {
            get { return _hasImage; }
            set { _hasImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(ImageDisplay)); }
        }

        /// <summary>"yes" / "" for the DataGrid Image column.</summary>
        public string ImageDisplay => HasImage ? "yes" : "";

        public string Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged(); }
        }

        /// <summary>Refreshes the row from a re-scan; a server tick keeps the row and its status.</summary>
        internal void SetContents(IReadOnlyList<string> maskNames, IReadOnlyList<string> doseNames, bool hasImage)
        {
            MaskBaseNames = maskNames.ToList();
            string masks = string.Join(", ", maskNames);
            string doses = string.Join(", ", doseNames);
            if (MaskNames != masks) MaskNames = masks;
            if (MaskCount != maskNames.Count) MaskCount = maskNames.Count;
            if (DoseNames != doses) DoseNames = doses;
            if (DoseCount != doseNames.Count) DoseCount = doseNames.Count;
            if (HasImage != hasImage) HasImage = hasImage;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
