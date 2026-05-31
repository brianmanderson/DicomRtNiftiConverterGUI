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
using CommunityToolkit.Mvvm.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the "NIfTI to DICOM" window. Pointed at a folder, it scans for DICOM
    /// folders containing a "masks/"/"doses/" subdirectory or an image.nii.gz and converts
    /// each in batch.
    ///
    /// Ported from WPF. The always-on server (file-watcher) mode and the Help window are
    /// deferred to later Phase 4 increments; the conversion logic is unchanged. Commands use
    /// CommunityToolkit.Mvvm (Avalonia has no WPF CommandManager auto-requery, so
    /// RefreshCommands() raises CanExecuteChanged explicitly).
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
        private CancellationTokenSource _cts;

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

            BrowseRootFolderCommand = new AsyncRelayCommand(BrowseRootFolderAsync, () => !IsBusy);
            ScanRootFolderCommand = new RelayCommand(DiscoverJobs,
                () => !IsBusy && !string.IsNullOrEmpty(RootFolder));
            ConvertCommand = new AsyncRelayCommand(ConvertAllAsync,
                () => !IsBusy && DiscoveredJobs.Count > 0 && (ConvertStructures || ConvertDoses || ConvertImage));
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
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

        /// <summary>One row per DICOM folder eligible for conversion.</summary>
        public ObservableCollection<NiftiToDicomJob> DiscoveredJobs { get; }

        public IAsyncRelayCommand BrowseRootFolderCommand { get; }
        public IRelayCommand ScanRootFolderCommand { get; }
        public IAsyncRelayCommand ConvertCommand { get; }
        public IRelayCommand CancelCommand { get; }

        private void RefreshCommands()
        {
            BrowseRootFolderCommand.NotifyCanExecuteChanged();
            ScanRootFolderCommand.NotifyCanExecuteChanged();
            ConvertCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
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
        /// Populates DiscoveredJobs by checking the root folder and its first-level subfolders
        /// for a "masks/" and/or "doses/" subdirectory (or an image.nii.gz). Each match is a job.
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

            if (HasConvertibleSubdir(RootFolder))
                AddJobIfValid(RootFolder);

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

        private void Cancel() => _cts?.Cancel();

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
