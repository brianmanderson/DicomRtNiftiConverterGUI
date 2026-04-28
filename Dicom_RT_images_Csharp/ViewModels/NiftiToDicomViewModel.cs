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
using System.Windows.Threading;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the "NIfTI to DICOM" window.
    /// Pointing the user at a single folder, this scans for DICOM folders that contain
    /// either a "masks/" subdirectory (for RT-STRUCT generation) and/or a "doses/"
    /// subdirectory (for RT-DOSE generation), and converts each in batch. The reference
    /// image series is auto-detected (first CT/MR/PT series found in each folder).
    /// </summary>
    public class NiftiToDicomViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly RtStructWriterService _rtStructWriter;
        private readonly RtDoseWriterService _rtDoseWriter;

        private string _rootFolder = "";
        private string _statusText = "Browse to a folder. Each DICOM folder containing a 'masks/' or 'doses/' subdirectory will be converted.";
        private bool _isBusy;
        private bool _convertStructures = true;
        private bool _convertDoses = true;
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
            RtDoseWriterService rtDoseWriter)
        {
            _scannerService = scannerService;
            _rtStructWriter = rtStructWriter;
            _rtDoseWriter = rtDoseWriter;

            DiscoveredJobs = new ObservableCollection<NiftiToDicomJob>();

            BrowseRootFolderCommand = new RelayCommand(_ => BrowseRootFolder(), _ => !IsBusy);
            ScanRootFolderCommand = new RelayCommand(_ => DiscoverJobs(),
                _ => !IsBusy && !string.IsNullOrEmpty(RootFolder));
            ConvertCommand = new RelayCommand(async _ => await ConvertAllAsync(),
                _ => !IsBusy
                     && !IsServerMode
                     && DiscoveredJobs.Count > 0
                     && (ConvertStructures || ConvertDoses));
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
            RunServerCommand = new RelayCommand(_ => ToggleServer(),
                _ => !IsBusy
                     && (ConvertStructures || ConvertDoses)
                     && !string.IsNullOrEmpty(RootFolder));
            OpenHelpCommand = new RelayCommand(p => OpenHelpWindow(p as System.Windows.Window));
        }

        private void OpenHelpWindow(System.Windows.Window owner)
        {
            var window = new Views.NiftiToDicomHelpWindow();
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }

        public string RootFolder
        {
            get { return _rootFolder; }
            set { _rootFolder = value; OnPropertyChanged(); }
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

        /// <summary>When true, masks/*.nii.gz files are converted to RT-STRUCT.</summary>
        public bool ConvertStructures
        {
            get { return _convertStructures; }
            set { _convertStructures = value; OnPropertyChanged(); }
        }

        /// <summary>When true, doses/*.nii.gz files are converted to RT-DOSE.</summary>
        public bool ConvertDoses
        {
            get { return _convertDoses; }
            set { _convertDoses = value; OnPropertyChanged(); }
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
            }
        }

        /// <summary>"Run Server" / "Stop Server" — bound to the server toggle button.</summary>
        public string ServerButtonText => IsServerMode ? "Stop Server" : "Run Server";

        /// <summary>One row per DICOM folder eligible for conversion.</summary>
        public ObservableCollection<NiftiToDicomJob> DiscoveredJobs { get; }

        public ICommand BrowseRootFolderCommand { get; }
        public ICommand ScanRootFolderCommand { get; }
        public ICommand ConvertCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RunServerCommand { get; }
        public ICommand OpenHelpCommand { get; }

        // ------- private helpers -------

        private void BrowseRootFolder()
        {
            // Match the MainWindow folder-picker convention.
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a DICOM folder (or parent folder containing several DICOM folders)",
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select Folder"
            };
            if (!string.IsNullOrEmpty(RootFolder) && Directory.Exists(RootFolder))
                dialog.InitialDirectory = RootFolder;

            if (dialog.ShowDialog() != true) return;

            RootFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
            // Folder selection no longer auto-scans — the user clicks the separate Scan button
            // (mirrors the DICOM → NIfTI window's Browse + Scan pattern).
            StatusText = "Folder set. Click 'Scan' to discover convertible folders.";
        }

        /// <summary>
        /// Populates DiscoveredJobs by checking the root folder and its first-level subfolders
        /// for the presence of a "masks/" and/or "doses/" subdirectory. Each match becomes a job.
        /// </summary>
        private void DiscoverJobs()
        {
            DiscoveredJobs.Clear();
            if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            {
                StatusText = "Folder does not exist.";
                return;
            }

            // 1. Is the root itself a convertible folder?
            if (HasConvertibleSubdir(RootFolder))
                AddJobIfValid(RootFolder);

            // 2. Otherwise (or additionally), scan first-level subfolders.
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(RootFolder))
                {
                    string leaf = Path.GetFileName(sub);
                    if (string.Equals(leaf, "masks", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(leaf, "doses", StringComparison.OrdinalIgnoreCase)) continue;
                    if (HasConvertibleSubdir(sub))
                        AddJobIfValid(sub);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error scanning subfolders: {ex.Message}";
                return;
            }

            if (DiscoveredJobs.Count == 0)
            {
                StatusText = "No DICOM folders with a 'masks/' or 'doses/' subdirectory were found.";
            }
            else
            {
                int totalMasks = DiscoveredJobs.Sum(j => j.MaskCount);
                int totalDoses = DiscoveredJobs.Sum(j => j.DoseCount);
                StatusText = $"Found {DiscoveredJobs.Count} DICOM folder(s): {totalMasks} mask(s), {totalDoses} dose(s) total. Click Convert to proceed.";
            }
        }

        private static bool HasConvertibleSubdir(string folder)
        {
            return GetMaskFiles(folder).Count > 0 || GetDoseFiles(folder).Count > 0;
        }

        private static List<string> GetMaskFiles(string folder)
        {
            string masksDir = Path.Combine(folder, "masks");
            if (!Directory.Exists(masksDir)) return new List<string>();
            return Directory.EnumerateFiles(masksDir, "*.nii.gz", SearchOption.TopDirectoryOnly).ToList();
        }

        private static List<string> GetDoseFiles(string folder)
        {
            string dosesDir = Path.Combine(folder, "doses");
            if (!Directory.Exists(dosesDir)) return new List<string>();
            return Directory.EnumerateFiles(dosesDir, "*.nii.gz", SearchOption.TopDirectoryOnly).ToList();
        }

        private static string StripNiiGz(string fileName)
        {
            string name = Path.GetFileName(fileName);
            if (name.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ".nii.gz".Length);
            return Path.GetFileNameWithoutExtension(name);
        }

        private void AddJobIfValid(string dicomFolder)
        {
            var maskFiles = GetMaskFiles(dicomFolder);
            var doseFiles = GetDoseFiles(dicomFolder);

            var maskNames = maskFiles
                .Select(StripNiiGz)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var doseNames = doseFiles
                .Select(StripNiiGz)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string outputName = $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm";

            DiscoveredJobs.Add(new NiftiToDicomJob
            {
                DicomFolder = dicomFolder,
                FolderDisplayName = Path.GetFileName(dicomFolder),
                MaskNames = string.Join(", ", maskNames),
                MaskCount = maskNames.Count,
                DoseNames = string.Join(", ", doseNames),
                DoseCount = doseNames.Count,
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

                    job.Status = "Scanning DICOM...";
                    StatusText = $"Processing {job.FolderDisplayName}: scanning DICOM...";

                    DicomSeriesGroup refSeries = null;
                    try
                    {
                        refSeries = await Task.Run(async () =>
                            await PickReferenceSeriesAsync(job.DicomFolder, _cts.Token).ConfigureAwait(false))
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        job.Status = $"FAILED — scan: {ex.Message}";
                        fail++;
                        continue;
                    }

                    if (refSeries == null)
                    {
                        job.Status = "FAILED — no CT/MR/PT image series found.";
                        fail++;
                        continue;
                    }

                    var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");
                    string folder = job.DicomFolder;
                    var summary = new List<string>();
                    bool jobFailed = false;
                    bool didAnything = false;

                    // 1. RT-STRUCT (if any masks AND user chose to convert structures)
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
                                    folder, refSeries, outPath, progress, _cts.Token)).ConfigureAwait(true);
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

                    // 2. RT-DOSE (if any doses AND user chose to convert doses)
                    if (job.DoseCount > 0 && ConvertDoses)
                    {
                        didAnything = true;
                        try
                        {
                            job.Status = "Converting doses...";
                            StatusText = $"{job.FolderDisplayName}: building RT-DOSE files...";
                            var written = await Task.Run(() =>
                                _rtDoseWriter.ConvertDoseFolderToRtDoses(
                                    folder, refSeries, progress, _cts.Token)).ConfigureAwait(true);
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
                        // Not counted as success or failure.
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

        /// <summary>
        /// Scans a single DICOM folder and returns the first CT/MR/PT image series found, or null.
        /// </summary>
        private async Task<DicomSeriesGroup> PickReferenceSeriesAsync(string dicomFolder, CancellationToken ct)
        {
            var patients = await _scannerService.ScanFolderAsync(dicomFolder, null, ct).ConfigureAwait(false);
            foreach (var patient in patients)
            {
                foreach (var study in patient.Studies)
                {
                    foreach (var series in study.Series)
                    {
                        if (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT")
                            return series;
                    }
                }
            }
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
            string sigMasks = SignatureForDir(Path.Combine(dicomFolder, "masks"), "*.nii.gz", SearchOption.TopDirectoryOnly);
            string sigDoses = SignatureForDir(Path.Combine(dicomFolder, "doses"), "*.nii.gz", SearchOption.TopDirectoryOnly);
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
            // Auto-detect the reference image series.
            DicomSeriesGroup refSeries;
            try
            {
                refSeries = await Task.Run(async () =>
                    await PickReferenceSeriesAsync(job.DicomFolder, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                job.Status = $"FAILED — scan: {ex.Message}";
                return JobOutcome.Failed;
            }

            if (refSeries == null)
            {
                job.Status = "FAILED — no CT/MR/PT image series found.";
                return JobOutcome.Failed;
            }

            string folder = job.DicomFolder;
            var summary = new List<string>();
            bool jobFailed = false;
            bool didAnything = false;

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
                                folder, refSeries, structOutPath, progress, CancellationToken.None))
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
                            useStableHashNames: true, skipIfExists: true))
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

        private void Cancel()
        {
            _cts?.Cancel();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// One row in the NiftiToDicom batch list: a DICOM folder + its discovered masks/doses + status.
    /// </summary>
    public class NiftiToDicomJob : INotifyPropertyChanged
    {
        private string _status = "Pending";

        public string DicomFolder { get; set; } = "";
        public string FolderDisplayName { get; set; } = "";
        public string MaskNames { get; set; } = "";
        public int MaskCount { get; set; }
        public string DoseNames { get; set; } = "";
        public int DoseCount { get; set; }
        public string OutputPath { get; set; } = "";

        public string Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
