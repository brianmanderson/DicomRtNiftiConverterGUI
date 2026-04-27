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
    /// ViewModel for the "NIfTI to DICOM" window.
    /// Pointing the user at a single folder, this scans for DICOM folders containing a
    /// "masks/" subdirectory (either the folder itself, or any first-level subfolder)
    /// and converts each to its own RT-STRUCT, written into the same DICOM folder.
    /// The reference image series is auto-detected (first CT/MR/PT series found).
    /// </summary>
    public class NiftiToDicomViewModel : INotifyPropertyChanged
    {
        private readonly DicomScannerService _scannerService;
        private readonly RtStructWriterService _rtStructWriter;

        private string _rootFolder = "";
        private string _statusText = "Browse to a folder. Each DICOM folder containing a 'masks/' subdirectory will be converted in batch.";
        private bool _isBusy;
        private CancellationTokenSource _cts;

        public NiftiToDicomViewModel(DicomScannerService scannerService, RtStructWriterService rtStructWriter)
        {
            _scannerService = scannerService;
            _rtStructWriter = rtStructWriter;

            DiscoveredJobs = new ObservableCollection<NiftiToDicomJob>();

            BrowseRootFolderCommand = new RelayCommand(_ => BrowseRootFolder(), _ => !IsBusy);
            ConvertCommand = new RelayCommand(async _ => await ConvertAllAsync(),
                _ => !IsBusy && DiscoveredJobs.Count > 0);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
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

        /// <summary>One row per DICOM folder eligible for conversion.</summary>
        public ObservableCollection<NiftiToDicomJob> DiscoveredJobs { get; }

        public ICommand BrowseRootFolderCommand { get; }
        public ICommand ConvertCommand { get; }
        public ICommand CancelCommand { get; }

        // ------- private helpers -------

        private void BrowseRootFolder()
        {
            // Match the MainWindow folder-picker convention: use OpenFileDialog with "Select Folder" trick.
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
            DiscoverJobs();
        }

        /// <summary>
        /// Populates DiscoveredJobs by checking the root folder and its first-level subfolders
        /// for the presence of a "masks/" subdirectory. Each match becomes a job.
        /// </summary>
        private void DiscoverJobs()
        {
            DiscoveredJobs.Clear();
            if (string.IsNullOrEmpty(RootFolder) || !Directory.Exists(RootFolder))
            {
                StatusText = "Folder does not exist.";
                return;
            }

            // 1. Is the root itself a DICOM folder with masks/?
            if (HasMasksSubdir(RootFolder))
            {
                AddJobIfValid(RootFolder);
            }

            // 2. Otherwise (or additionally), scan first-level subfolders.
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(RootFolder))
                {
                    if (string.Equals(Path.GetFileName(sub), "masks", StringComparison.OrdinalIgnoreCase))
                        continue; // skip the masks/ folder itself
                    if (HasMasksSubdir(sub))
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
                StatusText = "No DICOM folders with a 'masks/' subdirectory were found.";
            }
            else
            {
                int totalRois = DiscoveredJobs.Sum(j => j.MaskCount);
                StatusText = $"Found {DiscoveredJobs.Count} DICOM folder(s), {totalRois} mask(s) total. Click Convert to write RT-STRUCTs.";
            }
        }

        private static bool HasMasksSubdir(string folder)
        {
            string masksDir = Path.Combine(folder, "masks");
            if (!Directory.Exists(masksDir)) return false;
            return Directory.EnumerateFiles(masksDir, "*.nii.gz", SearchOption.TopDirectoryOnly).Any();
        }

        private void AddJobIfValid(string dicomFolder)
        {
            string masksDir = Path.Combine(dicomFolder, "masks");
            var maskNames = Directory.EnumerateFiles(masksDir, "*.nii.gz", SearchOption.TopDirectoryOnly)
                                     .Select(f =>
                                     {
                                         string name = Path.GetFileName(f);
                                         if (name.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
                                             return name.Substring(0, name.Length - ".nii.gz".Length);
                                         return Path.GetFileNameWithoutExtension(name);
                                     })
                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

            string outputName = $"RTSTRUCT_{DateTime.Now:yyyyMMdd_HHmmss}.dcm";

            DiscoveredJobs.Add(new NiftiToDicomJob
            {
                DicomFolder = dicomFolder,
                FolderDisplayName = Path.GetFileName(dicomFolder),
                MaskNames = string.Join(", ", maskNames),
                MaskCount = maskNames.Count,
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

                    try
                    {
                        // Auto-detect the reference image series by scanning the DICOM folder.
                        DicomSeriesGroup refSeries = await Task.Run(async () =>
                            await PickReferenceSeriesAsync(job.DicomFolder, _cts.Token).ConfigureAwait(false))
                            .ConfigureAwait(true);

                        if (refSeries == null)
                        {
                            job.Status = "FAILED — no CT/MR/PT image series found.";
                            fail++;
                            continue;
                        }

                        job.Status = "Converting...";
                        StatusText = $"Processing {job.FolderDisplayName}: building RT-STRUCT...";

                        var progress = new Progress<string>(msg => StatusText = $"{job.FolderDisplayName}: {msg}");

                        string folder = job.DicomFolder;
                        string outPath = job.OutputPath;
                        string written = await Task.Run(() =>
                            _rtStructWriter.ConvertMasksFolderToRtStruct(
                                folder, refSeries, outPath, progress, _cts.Token)).ConfigureAwait(true);

                        job.Status = $"OK ({Path.GetFileName(written)})";
                        success++;
                    }
                    catch (OperationCanceledException)
                    {
                        job.Status = "Cancelled";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        job.Status = $"FAILED — {ex.Message}";
                        fail++;
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
    /// One row in the NiftiToDicom batch list: a DICOM folder + its discovered masks + its target output path.
    /// </summary>
    public class NiftiToDicomJob : INotifyPropertyChanged
    {
        private string _status = "Pending";

        public string DicomFolder { get; set; } = "";
        public string FolderDisplayName { get; set; } = "";
        public string MaskNames { get; set; } = "";
        public int MaskCount { get; set; }
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
