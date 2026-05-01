using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;
using FellowOakDicom;

namespace Dicom_RT_images_Csharp.Cli
{
    /// <summary>
    /// Headless command-line entry point for benchmark / batch use.
    ///
    /// Detected and dispatched from <see cref="App.OnStartup"/> when the first
    /// argument is <c>--headless</c>. Reuses the existing services so behaviour
    /// is identical to the GUI's Convert Selected / NIfTI-to-DICOM workflows.
    ///
    /// Usage:
    ///
    ///   Forward (RTSTRUCT -> per-ROI binary masks):
    ///       Dicom_RT_images_Csharp.exe --headless --forward
    ///           --rtstruct PATH
    ///           --image-folder PATH
    ///           --output-folder PATH
    ///
    ///   Reverse (per-ROI binary masks -> RTSTRUCT):
    ///       Dicom_RT_images_Csharp.exe --headless --reverse
    ///           --image-folder PATH
    ///           --masks-folder PATH
    ///           --output PATH
    ///
    /// stdout: machine-readable lines describing every output file written.
    /// stderr: human-readable progress / error messages.
    /// Exit code: 0 on success, non-zero on failure.
    /// </summary>
    public static class HeadlessRunner
    {
        // Win32 APIs used to attach this WPF .exe to the parent process's console
        // so that Console.WriteLine works the way a CLI user expects. Without
        // this, the WPF subsystem swallows stdout / stderr.
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
        private const int ATTACH_PARENT_PROCESS = -1;

        /// <summary>
        /// Parse <paramref name="args"/> and dispatch to the requested headless command.
        /// </summary>
        public static int Run(string[] args)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            try
            {
                if (HasFlag(args, "--forward"))
                {
                    return RunForward(args);
                }
                if (HasFlag(args, "--reverse"))
                {
                    return RunReverse(args);
                }
                Console.Error.WriteLine("Headless mode requires --forward or --reverse.");
                PrintUsage();
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        // -----------------------------------------------------------------
        //  Forward: RTSTRUCT + image series -> per-ROI binary masks
        // -----------------------------------------------------------------

        private static int RunForward(string[] args)
        {
            string rtstructPath  = RequireArg(args, "--rtstruct");
            string imageFolder   = RequireArg(args, "--image-folder");
            string outputFolder  = RequireArg(args, "--output-folder");

            if (!File.Exists(rtstructPath))
                throw new FileNotFoundException($"RTSTRUCT not found: {rtstructPath}");
            if (!Directory.Exists(imageFolder))
                throw new DirectoryNotFoundException($"Image folder not found: {imageFolder}");

            Directory.CreateDirectory(outputFolder);

            // Build the two DicomSeriesGroup objects the conversion service expects.
            // We don't need the full scanner -- the inputs are explicit.
            var imageSeries = BuildImageSeriesFromFolder(imageFolder);
            var rtStructSeries = BuildRtStructSeriesFromFile(rtstructPath);

            Console.Error.WriteLine($"  Image series: {imageSeries.FilePaths.Count} files in {imageFolder}");
            Console.Error.WriteLine($"  RTSTRUCT:    {rtstructPath} ({rtStructSeries.RoiNames.Count} ROIs)");
            Console.Error.WriteLine($"  Output:      {outputFolder}");

            var maskService       = new RtStructMaskService();
            var conversionService = new NiftiConversionService(maskService);

            var progress = new Progress<string>(msg => Console.Error.WriteLine(msg));

            // No ROI associations / no resampling: keep the conversion as faithful
            // as possible to the input. This matches the benchmark harness's
            // intent of measuring the rasterizer in isolation.
            var roiVolumes = conversionService.ConvertStructToNifti(
                rtStructSeries: rtStructSeries,
                imageSeries: imageSeries,
                outputDir: outputFolder,
                associations: null,
                exportUnmatched: true,
                flatOutput: true,            // write masks/<roi>.nii.gz directly into outputFolder
                progress: progress,
                ct: CancellationToken.None,
                targetSpacing: null);

            if (roiVolumes == null)
            {
                Console.Error.WriteLine("Conversion produced no masks.");
                return 1;
            }

            // Machine-readable summary on stdout: one row per ROI: <name>\t<volume_cc>\t<path>.
            Console.Out.WriteLine("# rt_mask_validation forward");
            foreach (var kvp in roiVolumes)
            {
                string maskPath = Path.Combine(outputFolder, SanitizeFileName(kvp.Key) + ".nii.gz");
                Console.Out.WriteLine($"{kvp.Key}\t{kvp.Value:G}\t{maskPath}");
            }
            return 0;
        }

        // -----------------------------------------------------------------
        //  Reverse: per-ROI binary masks -> RTSTRUCT
        // -----------------------------------------------------------------

        private static int RunReverse(string[] args)
        {
            string imageFolder = RequireArg(args, "--image-folder");
            string masksFolder = RequireArg(args, "--masks-folder");
            string outputPath  = RequireArg(args, "--output");

            if (!Directory.Exists(imageFolder))
                throw new DirectoryNotFoundException($"Image folder not found: {imageFolder}");
            if (!Directory.Exists(masksFolder))
                throw new DirectoryNotFoundException($"Masks folder not found: {masksFolder}");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));

            // RtStructWriterService expects the masks at <dicomFolder>/masks/. If the
            // user pointed --masks-folder somewhere else, we link / copy the .nii.gz
            // files into a temporary "<imageFolder>/masks/" mirror before invoking
            // the service. We use a *staging directory* that mirrors imageFolder
            // so we never modify the input tree.
            string stagingFolder = CreateMasksStagingFolder(imageFolder, masksFolder);

            try
            {
                var imageSeries = BuildImageSeriesFromFolder(imageFolder);

                var writer = new RtStructWriterService();
                var progress = new Progress<string>(msg => Console.Error.WriteLine(msg));

                Console.Error.WriteLine($"  Image series: {imageSeries.FilePaths.Count} files in {imageFolder}");
                Console.Error.WriteLine($"  Masks folder: {masksFolder}");
                Console.Error.WriteLine($"  Output:       {outputPath}");

                string written = writer.ConvertMasksFolderToRtStruct(
                    dicomFolder: stagingFolder,
                    referenceSeries: imageSeries,
                    outputPath: outputPath,
                    progress: progress,
                    ct: CancellationToken.None);

                Console.Out.WriteLine("# rt_mask_validation reverse");
                Console.Out.WriteLine(written);
                return 0;
            }
            finally
            {
                TryCleanupStaging(stagingFolder);
            }
        }

        // -----------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------

        private static DicomSeriesGroup BuildImageSeriesFromFolder(string folder)
        {
            var dcmFiles = Directory
                .EnumerateFiles(folder, "*.dcm", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dcmFiles.Count == 0)
                throw new InvalidOperationException($"No .dcm files in {folder}.");

            // Read tags from the first file to populate series metadata.
            var first = DicomFile.Open(dcmFiles[0], FileReadOption.SkipLargeTags).Dataset;
            return new DicomSeriesGroup
            {
                SeriesInstanceUID    = GetStringOrEmpty(first, DicomTag.SeriesInstanceUID),
                SeriesDescription    = GetStringOrEmpty(first, DicomTag.SeriesDescription),
                Modality             = GetStringOrEmpty(first, DicomTag.Modality),
                SeriesDate           = GetStringOrEmpty(first, DicomTag.SeriesDate),
                FrameOfReferenceUID  = GetStringOrEmpty(first, DicomTag.FrameOfReferenceUID),
                FilePaths            = dcmFiles,
            };
        }

        private static DicomSeriesGroup BuildRtStructSeriesFromFile(string rtstructPath)
        {
            var dcm = DicomFile.Open(rtstructPath).Dataset;
            var series = new DicomSeriesGroup
            {
                SeriesInstanceUID   = GetStringOrEmpty(dcm, DicomTag.SeriesInstanceUID),
                SeriesDescription   = GetStringOrEmpty(dcm, DicomTag.SeriesDescription),
                Modality            = GetStringOrEmpty(dcm, DicomTag.Modality),
                SeriesDate          = GetStringOrEmpty(dcm, DicomTag.SeriesDate),
                FrameOfReferenceUID = GetStringOrEmpty(dcm, DicomTag.FrameOfReferenceUID),
                FilePaths           = new List<string> { rtstructPath },
            };

            if (dcm.Contains(DicomTag.StructureSetROISequence))
            {
                var seq = dcm.GetSequence(DicomTag.StructureSetROISequence);
                foreach (var item in seq.Items)
                {
                    if (item.Contains(DicomTag.ROIName))
                    {
                        string name = item.GetSingleValueOrDefault(DicomTag.ROIName, "");
                        if (!string.IsNullOrWhiteSpace(name))
                            series.RoiNames.Add(name);
                    }
                }
            }
            return series;
        }

        /// <summary>
        /// Create a temporary directory ``stage/`` next to ``imageFolder`` containing:
        ///   * symbolic links / copies of every .dcm in ``imageFolder``
        ///   * a ``masks/`` subdirectory with every .nii.gz from ``masksFolder``
        /// so that <see cref="RtStructWriterService"/> -- which expects the masks at
        /// ``&lt;dicomFolder&gt;/masks/`` -- can run unchanged.
        /// </summary>
        private static string CreateMasksStagingFolder(string imageFolder, string masksFolder)
        {
            string stage = Path.Combine(
                Path.GetTempPath(),
                "rt_mask_validation_stage_" + Path.GetRandomFileName());
            Directory.CreateDirectory(stage);
            string masksDir = Path.Combine(stage, "masks");
            Directory.CreateDirectory(masksDir);

            // Hard-link the CT slices (fast, no disk space) -- fall back to copy on
            // exotic file systems where hard-linking fails.
            foreach (var src in Directory.EnumerateFiles(imageFolder, "*.dcm", SearchOption.TopDirectoryOnly))
            {
                string dst = Path.Combine(stage, Path.GetFileName(src));
                if (!CreateHardLink(dst, src, IntPtr.Zero))
                    File.Copy(src, dst);
            }
            foreach (var src in Directory.EnumerateFiles(masksFolder, "*.nii.gz", SearchOption.TopDirectoryOnly))
            {
                string dst = Path.Combine(masksDir, Path.GetFileName(src));
                if (!CreateHardLink(dst, src, IntPtr.Zero))
                    File.Copy(src, dst);
            }
            return stage;
        }

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(
            string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private static void TryCleanupStaging(string stage)
        {
            try { Directory.Delete(stage, recursive: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (warn) failed to clean staging '{stage}': {ex.Message}");
            }
        }

        private static string GetStringOrEmpty(DicomDataset ds, DicomTag tag)
        {
            try { return ds.GetSingleValueOrDefault(tag, ""); }
            catch { return ""; }
        }

        private static bool HasFlag(string[] args, string flag) =>
            args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        private static string RequireArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            throw new ArgumentException($"Required argument '{name}' is missing.");
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) name = name.Replace(c, '_');
            return name;
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Forward (RTSTRUCT -> per-ROI masks):");
            Console.Error.WriteLine("  --headless --forward --rtstruct PATH --image-folder PATH --output-folder PATH");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Reverse (per-ROI masks -> RTSTRUCT):");
            Console.Error.WriteLine("  --headless --reverse --image-folder PATH --masks-folder PATH --output PATH");
        }
    }
}
