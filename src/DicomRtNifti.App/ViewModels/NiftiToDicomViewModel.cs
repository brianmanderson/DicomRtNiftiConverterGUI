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
    /// </summary>
    public class NiftiToDicomViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly RtStructWriterService _rtStructWriter;
        private readonly RtDoseWriterService _rtDoseWriter;
        private readonly NiftiMetadataService _metadataService;
        private readonly NiftiImageWriterService _imageWriter;
        private readonly IFolderPicker _folderPicker;

        private string _rootFolder = "";
        private string _statusText = "Browse to a folder. Each DICOM folder containing a 'masks/' or 'doses/' subdirectory (or an image.nii.gz) will be converted.";
        private bool _isBusy;
        private bool _convertStructures = true;
        private bool _convertDoses = true;
        private bool _convertImage = true;
        private bool _isServerMode;
        private CancellationTokenSource _cts;

        // Server-mode state
        private const int ServerIntervalSeconds = 10;
        private DispatcherTimer _serverTimer;
        private bool _tickInFlight;
        private readonly Dictionary<string, FolderFingerprint> _fingerprints
            = new Dictionary<string, FolderFingerprint>(StringComparer.OrdinalIgnoreCase);

        public NiftiToDicomViewModel(
            DicomScannerService scannerService,
            RtStructWriterService rtStructWriter,
            RtDoseWriterService rtDoseWriter,
            NiftiMetadataService metadataService,
            NiftiImageWriterService imageWriter,
            IFolderPicker folderPicker)
        {
            _scannerService = scannerService;
            _rtStructWriter = rtStructWriter;
            _rtDoseWriter = rtDoseWriter;
            _metadataService = metadataService;
            _imageWriter = imageWriter;
            _folderPicker = folderPicker;

            DiscoveredJobs = new ObservableCollection<NiftiToDicomJob>();
            DiscoveredJobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowJobsEmptyHint));

            BrowseRootFolderCommand = new AsyncRelayCommand(BrowseRootFolderAsync, () => !IsBusy && !IsServerMode);
            ScanRootFolderCommand = new RelayCommand(DiscoverJobs,
                () => !IsBusy && !IsServerMode && !string.IsNullOrEmpty(RootFolder));
            ConvertCommand = new AsyncRelayCommand(ConvertAllAsync,
                () => !IsBusy && !IsServerMode && DiscoveredJobs.Count > 0 && (ConvertStructures || ConvertDoses || ConvertImage));
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            RunServerCommand = new RelayCommand(ToggleServer,
                () => !IsBusy && (ConvertStructures || ConvertDoses || ConvertImage) && !string.IsNullOrEmpty(RootFolder));
            OpenHelpCommand = new RelayCommand(OpenHelp);
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
            set { _rootFolder = value; OnPropertyChanged(); RefreshCommands(); }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set { _isBusy = value; OnPropertyChanged(); RefreshCommands(); }
        }

        /// <summary>When true, masks/*.nii.gz files are converted to RT-STRUCT.</summary>
        public bool ConvertStructures
        {
            get { return _convertStructures; }
            set { _convertStructures = value; OnPropertyChanged(); RefreshCommands(); }
        }

        /// <summary>When true, doses/*.nii.gz files are converted to RT-DOSE.</summary>
        public bool ConvertDoses
        {
            get { return _convertDoses; }
            set { _convertDoses = value; OnPropertyChanged(); RefreshCommands(); }
        }

        /// <summary>When true, an image.nii.gz file is converted to a DICOM image series first.</summary>
        public bool ConvertImage
        {
            get { return _convertImage; }
            set { _convertImage = value; OnPropertyChanged(); RefreshCommands(); }
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
        public IRelayCommand ScanRootFolderCommand { get; }
        public IAsyncRelayCommand ConvertCommand { get; }
        public IRelayCommand CancelCommand { get; }
        public IRelayCommand RunServerCommand { get; }
        public IRelayCommand OpenHelpCommand { get; }

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
        /// Each match is a job.
        /// </summary>
        private void DiscoverJobs()
        {
            DiscoveredJobs.Clear();
            if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            {
                StatusText = "Folder does not exist.";
                RefreshCommands();
                return;
            }

            try
            {
                foreach (var folder in EnumerateCandidateFolders(RootFolder))
                {
                    if (HasConvertibleSubdir(folder))
                        AddJobIfValid(folder);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error scanning subfolders: {ex.Message}";
                RefreshCommands();
                return;
            }

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

            RefreshCommands();
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

        private static bool HasConvertibleSubdir(string folder)
            => GetMaskFiles(folder).Count > 0 || GetDoseFiles(folder).Count > 0 || HasImageNifti(folder);

        private static List<string> GetMaskFiles(string folder)
            => NiftiFileNaming.EnumerateNiftiFiles(Path.Combine(folder, "masks")).ToList();

        private static List<string> GetDoseFiles(string folder)
            => NiftiFileNaming.EnumerateNiftiFiles(Path.Combine(folder, "doses")).ToList();

        private static bool HasImageNifti(string folder)
            => NiftiFileNaming.TryGetImageNiftiPath(folder, out _);

        private static string StripNiiGz(string fileName) => NiftiFileNaming.StripNiftiExtension(fileName);

        private void AddJobIfValid(string dicomFolder)
        {
            var maskFiles = GetMaskFiles(dicomFolder);
            var doseFiles = GetDoseFiles(dicomFolder);
            bool hasImage = HasImageNifti(dicomFolder);

            var maskNames = maskFiles.Select(StripNiiGz).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var doseNames = doseFiles.Select(StripNiiGz).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            string outputName = $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm";

            DiscoveredJobs.Add(new NiftiToDicomJob
            {
                DicomFolder = dicomFolder,
                FolderDisplayName = Path.GetFileName(dicomFolder),
                MaskNames = string.Join(", ", maskNames),
                MaskCount = maskNames.Count,
                DoseNames = string.Join(", ", doseNames),
                DoseCount = doseNames.Count,
                HasImage = hasImage,
                OutputPath = Path.Combine(dicomFolder, outputName),
                Status = "Pending"
            });
        }

        private async Task ConvertAllAsync()
        {
            if (DiscoveredJobs.Count == 0) return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            int success = 0, fail = 0;

            try
            {
                foreach (var job in DiscoveredJobs)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    string folder = job.DicomFolder;
                    var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                    var summary = new List<string>();
                    bool jobFailed = false;
                    bool didAnything = false;

                    NiftiPatientMetadata metadata = _metadataService.LoadOrSynthesize(folder);

                    if (job.HasImage && ConvertImage)
                    {
                        try
                        {
                            job.Status = "Converting image...";
                            StatusText = $"{job.FolderDisplayName}: writing image series...";
                            var imageWritten = await Task.Run(() =>
                                _imageWriter.ConvertImageNiftiToDicomSeries(
                                    folder, metadata, progress, _cts.Token)).ConfigureAwait(true);
                            if (imageWritten.Count > 0)
                            {
                                summary.Add($"image series ({imageWritten.Count} slices)");
                                didAnything = true;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"image FAILED: {ex.Message}");
                            jobFailed = true;
                        }
                    }
                    else if (job.HasImage && !ConvertImage)
                    {
                        summary.Add("image skipped");
                    }

                    job.Status = "Scanning DICOM...";
                    StatusText = $"Processing {job.FolderDisplayName}: scanning DICOM...";

                    DicomSeriesGroup refSeries = null;
                    try
                    {
                        refSeries = await Task.Run(async () =>
                            await PickReferenceSeriesAsync(folder, _cts.Token).ConfigureAwait(false))
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        job.Status = $"FAILED — scan: {ex.Message}";
                        fail++;
                        continue;
                    }

                    if (refSeries == null && (job.MaskCount > 0 || job.DoseCount > 0))
                    {
                        StatusText = $"{job.FolderDisplayName}: no reference DICOM — using metadata.json fallback.";
                    }

                    if (job.MaskCount > 0 && ConvertStructures)
                    {
                        didAnything = true;
                        try
                        {
                            job.Status = "Converting masks...";
                            StatusText = $"{job.FolderDisplayName}: building RT-STRUCT...";
                            string outPath = job.OutputPath;
                            string written = await Task.Run(() =>
                                _rtStructWriter.ConvertMasksFolderToRtStruct(
                                    folder, refSeries, outPath, progress, _cts.Token, metadata)).ConfigureAwait(true);
                            summary.Add($"1 RT-STRUCT ({Path.GetFileName(written)})");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"masks FAILED: {ex.Message}");
                            jobFailed = true;
                        }
                    }
                    else if (job.MaskCount > 0 && !ConvertStructures)
                    {
                        summary.Add("masks skipped");
                    }

                    if (job.DoseCount > 0 && ConvertDoses)
                    {
                        didAnything = true;
                        try
                        {
                            job.Status = "Converting doses...";
                            StatusText = $"{job.FolderDisplayName}: building RT-DOSE files...";
                            var written = await Task.Run(() =>
                                _rtDoseWriter.ConvertDoseFolderToRtDoses(
                                    folder, refSeries, progress, _cts.Token,
                                    useStableHashNames: false, skipIfExists: false,
                                    metadata: metadata)).ConfigureAwait(true);
                            summary.Add($"{written.Count} RT-DOSE");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            summary.Add($"doses FAILED: {ex.Message}");
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
                    }
                    else if (jobFailed)
                    {
                        job.Status = "FAILED — " + string.Join("; ", summary);
                        fail++;
                    }
                    else
                    {
                        job.Status = "OK — " + string.Join(", ", summary);
                        success++;
                    }
                }

                StatusText = $"Done. {success} succeeded, {fail} failed.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Conversion cancelled.";
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
            IsServerMode = true;
            _serverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(ServerIntervalSeconds)
            };
            _serverTimer.Tick += async (s, e) => await OnServerTickAsync().ConfigureAwait(true);
            _serverTimer.Start();

            StatusText = $"Server running — watching '{RootFolder}' every {ServerIntervalSeconds}s.";
            // Snapshot baseline immediately so the next tick can compare.
            _ = OnServerTickAsync();
        }

        private void StopServer()
        {
            _serverTimer?.Stop();
            _serverTimer = null;
            _fingerprints.Clear();
            IsServerMode = false;
            StatusText = "Server stopped.";
        }

        private async Task OnServerTickAsync()
        {
            if (_tickInFlight) return;
            _tickInFlight = true;
            try
            {
                // 1. Re-discover jobs from the current root.
                DiscoverJobs();
                if (DiscoveredJobs.Count == 0)
                {
                    StatusText = $"Server running — no convertible folders under '{RootFolder}'.";
                    return;
                }

                // 2. Compute per-job fingerprints, decide which are settled.
                var nowFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var settled = new List<NiftiToDicomJob>();
                foreach (var job in DiscoveredJobs)
                {
                    string sig = ComputeFolderSignature(job.DicomFolder);
                    nowFingerprints[job.DicomFolder] = sig;

                    if (_fingerprints.TryGetValue(job.DicomFolder, out var prev) && prev.Signature == sig)
                    {
                        settled.Add(job);
                    }
                    else
                    {
                        job.Status = "Watching — files changed, waiting for stability.";
                    }
                }

                // 3. Persist fingerprints for next tick (drop entries for folders that disappeared).
                _fingerprints.Clear();
                foreach (var kv in nowFingerprints)
                    _fingerprints[kv.Key] = new FolderFingerprint { Signature = kv.Value };

                if (settled.Count == 0)
                {
                    StatusText = $"Server running — {DiscoveredJobs.Count} folder(s), waiting for stability...";
                    return;
                }

                // 4. Run conversions for settled folders, in sequence.
                StatusText = $"Server running — {settled.Count} settled folder(s), running...";
                int converted = 0, upToDate = 0, failed = 0;
                foreach (var job in settled)
                {
                    var outcome = await RunSettledJobAsync(job).ConfigureAwait(true);
                    if (outcome == JobOutcome.Converted) converted++;
                    else if (outcome == JobOutcome.UpToDate) upToDate++;
                    else if (outcome == JobOutcome.Failed) failed++;
                }

                StatusText = $"Server running — last tick: {converted} converted, {upToDate} up-to-date, {failed} failed. Watching '{RootFolder}'.";
            }
            catch (Exception ex)
            {
                StatusText = $"Server tick error: {ex.Message}";
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

        private async Task<JobOutcome> RunSettledJobAsync(NiftiToDicomJob job)
        {
            string folder = job.DicomFolder;
            var summary = new List<string>();
            bool jobFailed = false;
            bool didAnything = false;

            // Step 0: load metadata.json (creating one if needed). The same UIDs persist
            // across the image / mask / dose passes.
            NiftiPatientMetadata metadata = _metadataService.LoadOrSynthesize(folder);

            // Step 1: convert image.nii.gz once. Subsequent ticks skip if the series UID
            // recorded in metadata.json is already on disk.
            if (job.HasImage && ConvertImage)
            {
                try
                {
                    var imageProgress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                    var imageWritten = await Task.Run(() =>
                        _imageWriter.ConvertImageNiftiToDicomSeries(
                            folder, metadata, imageProgress, CancellationToken.None))
                        .ConfigureAwait(true);
                    if (imageWritten.Count > 0)
                    {
                        summary.Add($"image series ({imageWritten.Count} slices)");
                        didAnything = true;
                    }
                }
                catch (Exception ex)
                {
                    summary.Add($"image FAILED: {ex.Message}");
                    jobFailed = true;
                }
            }

            // Step 2: scan for a reference image series — possibly the one we just wrote.
            DicomSeriesGroup refSeries;
            try
            {
                refSeries = await Task.Run(async () =>
                    await PickReferenceSeriesAsync(folder, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                job.Status = $"FAILED — scan: {ex.Message}";
                return JobOutcome.Failed;
            }

            // refSeries may be null if there's no image and no DICOMs — the writers handle
            // that case via the metadata fallback.

            // RT-STRUCT: hash from sorted mask basenames; skip if the resulting filename exists.
            if (job.MaskCount > 0 && ConvertStructures)
            {
                var maskBasenames = (job.MaskNames ?? "")
                    .Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                string structFileName = HashNaming.RtStructFileName(maskBasenames);
                string structOutPath = Path.Combine(folder, structFileName);

                if (File.Exists(structOutPath))
                {
                    summary.Add($"masks up-to-date ({structFileName})");
                }
                else
                {
                    didAnything = true;
                    try
                    {
                        var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                        string written = await Task.Run(() =>
                            _rtStructWriter.ConvertMasksFolderToRtStruct(
                                folder, refSeries, structOutPath, progress, CancellationToken.None, metadata))
                            .ConfigureAwait(true);
                        summary.Add($"RT-STRUCT ({Path.GetFileName(written)})");
                    }
                    catch (Exception ex)
                    {
                        summary.Add($"masks FAILED: {ex.Message}");
                        jobFailed = true;
                    }
                }
            }

            // RT-DOSE: per-file hash filename; skip-if-exists handled inside the writer.
            if (job.DoseCount > 0 && ConvertDoses)
            {
                try
                {
                    var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                    var beforeCount = Directory
                        .EnumerateFiles(folder, "RTDOSE_*.dcm", SearchOption.TopDirectoryOnly)
                        .Count();

                    var written = await Task.Run(() =>
                        _rtDoseWriter.ConvertDoseFolderToRtDoses(
                            folder, refSeries, progress, CancellationToken.None,
                            useStableHashNames: true, skipIfExists: true,
                            metadata: metadata))
                        .ConfigureAwait(true);

                    var afterCount = Directory
                        .EnumerateFiles(folder, "RTDOSE_*.dcm", SearchOption.TopDirectoryOnly)
                        .Count();
                    int newDoses = afterCount - beforeCount;
                    if (newDoses > 0) didAnything = true;

                    summary.Add(newDoses > 0
                        ? $"{newDoses} new RT-DOSE"
                        : $"doses up-to-date ({written.Count})");
                }
                catch (Exception ex)
                {
                    summary.Add($"doses FAILED: {ex.Message}");
                    jobFailed = true;
                }
            }

            if (jobFailed)
            {
                job.Status = "FAILED — " + string.Join("; ", summary);
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
            return JobOutcome.Converted;
        }

        private struct FolderFingerprint
        {
            public string Signature;
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

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One row in the batch list: a DICOM folder + its discovered masks/doses + status.</summary>
    public class NiftiToDicomJob : INotifyPropertyChanged
    {
        private string _status = "Pending";

        public string DicomFolder { get; set; } = "";
        public string FolderDisplayName { get; set; } = "";
        public string MaskNames { get; set; } = "";
        public int MaskCount { get; set; }
        public string DoseNames { get; set; } = "";
        public int DoseCount { get; set; }
        public bool HasImage { get; set; }
        public string OutputPath { get; set; } = "";

        /// <summary>"yes" / "" for the DataGrid Image column.</summary>
        public string ImageDisplay => HasImage ? "yes" : "";

        public string Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
