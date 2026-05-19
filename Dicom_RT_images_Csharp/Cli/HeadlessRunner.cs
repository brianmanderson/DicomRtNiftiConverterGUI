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
    ///   Forward (RTSTRUCT -> per-ROI binary masks; optionally also image.nii.gz and doses/):
    ///       Dicom_RT_images_Csharp.exe --headless --forward
    ///           --rtstruct PATH
    ///           --image-folder PATH
    ///           --output-folder PATH
    ///           [--include-image]    (also write image.nii.gz alongside masks)
    ///           [--rtdose PATH]      (also write doses/&lt;series description&gt;.nii.gz)
    ///
    ///   Reverse (per-ROI binary masks -> RTSTRUCT):
    ///       Dicom_RT_images_Csharp.exe --headless --reverse
    ///           --image-folder PATH
    ///           --masks-folder PATH
    ///           --output PATH
    ///
    ///   Image-reverse (NIfTI image volume -> DICOM image series):
    ///       Dicom_RT_images_Csharp.exe --headless --image-reverse
    ///           --nifti-image PATH
    ///           --output-folder PATH
    ///           [--modality {CT|MR|PT}]   (default: CT)
    ///           [--ref-dicom-folder PATH] (template for patient/study metadata)
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
                if (HasFlag(args, "--image-reverse"))
                {
                    return RunImageReverse(args);
                }
                Console.Error.WriteLine(
                    "Headless mode requires --forward, --reverse, or --image-reverse.");
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

            if (HasFlag(args, "--include-image"))
            {
                conversionService.ConvertImageSeriesToNifti(
                    series: imageSeries,
                    outputDir: outputFolder,
                    progress: progress,
                    ct: CancellationToken.None,
                    targetSpacing: null);
            }

            string rtdosePath = OptionalArg(args, "--rtdose");
            if (!string.IsNullOrEmpty(rtdosePath))
            {
                if (!File.Exists(rtdosePath))
                    throw new FileNotFoundException($"RT-DOSE file not found: {rtdosePath}");
                var doseSeries = BuildRtDoseSeriesFromFile(rtdosePath);
                Console.Error.WriteLine($"  RTDOSE:       {rtdosePath} (\"{doseSeries.SeriesDescription}\")");
                conversionService.ConvertDoseToNifti(
                    doseSeries: doseSeries,
                    outputDir: outputFolder,
                    progress: progress,
                    ct: CancellationToken.None,
                    targetSpacing: null);
            }

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
            string imageFolder = OptionalArg(args, "--image-folder");
            string masksFolder = RequireArg(args, "--masks-folder");
            string outputPath  = RequireArg(args, "--output");

            if (!Directory.Exists(masksFolder))
                throw new DirectoryNotFoundException($"Masks folder not found: {masksFolder}");
            if (!string.IsNullOrEmpty(imageFolder) && !Directory.Exists(imageFolder))
                throw new DirectoryNotFoundException($"Image folder not found: {imageFolder}");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));

            var writer = new RtStructWriterService();
            var progress = new Progress<string>(msg => Console.Error.WriteLine(msg));

            // Nifti-only path: --image-folder is omitted. The mask folder is the DICOM
            // root; metadata.json supplies patient/study/frame-of-reference. We honour
            // an --image-nifti flag (defaulting to <masks-folder>/image.nii.gz) so the
            // caller can opt into image.nii.gz -> DICOM series conversion too.
            if (string.IsNullOrEmpty(imageFolder))
            {
                return RunReverseNiftiOnly(masksFolder, outputPath, writer, progress, args);
            }

            // Reference-DICOM path: existing behaviour. RtStructWriterService expects the
            // masks at <dicomFolder>/masks/. Stage the inputs into a temporary mirror so
            // we don't modify the input tree.
            string stagingFolder = CreateMasksStagingFolder(imageFolder, masksFolder);

            try
            {
                var imageSeries = BuildImageSeriesFromFolder(imageFolder);

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

        /// <summary>
        /// Nifti-only reverse mode: no reference DICOM series. Stages masks under a
        /// temporary directory that becomes the dicomFolder; if --image-nifti points to a
        /// readable file, that image is converted to a DICOM CT series first so the
        /// RT-STRUCT can reference its per-slice SOPInstanceUIDs.
        /// </summary>
        private static int RunReverseNiftiOnly(
            string masksFolder, string outputPath,
            RtStructWriterService writer, IProgress<string> progress, string[] args)
        {
            string stage = Path.Combine(
                Path.GetTempPath(),
                "rt_mask_validation_stage_" + Path.GetRandomFileName());
            Directory.CreateDirectory(stage);
            string stagedMasks = Path.Combine(stage, "masks");
            Directory.CreateDirectory(stagedMasks);

            try
            {
                // Resolve image.nii.gz / image.nii first so we can exclude it from the mask
                // staging sweep -- otherwise it would be picked up as an "image" ROI.
                string imageNiftiArg = OptionalArg(args, "--image-nifti");
                string imageNifti;
                if (!string.IsNullOrEmpty(imageNiftiArg))
                    imageNifti = imageNiftiArg;
                else if (!NiftiFileNaming.TryGetImageNiftiPath(masksFolder, out imageNifti))
                    imageNifti = Path.Combine(masksFolder, NiftiFileNaming.ImageNiiGz);
                string imageNiftiFull = File.Exists(imageNifti) ? Path.GetFullPath(imageNifti) : null;

                foreach (var src in NiftiFileNaming.EnumerateNiftiFiles(masksFolder))
                {
                    // Skip the image volume; it must not be staged as a mask.
                    if (imageNiftiFull != null &&
                        string.Equals(Path.GetFullPath(src), imageNiftiFull, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // Also skip any dose.nii / dose.nii.gz that happens to share the folder.
                    string srcName = Path.GetFileName(src);
                    if (string.Equals(srcName, "dose.nii.gz", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(srcName, "dose.nii",   StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string dst = Path.Combine(stagedMasks, srcName);
                    if (!CreateHardLink(dst, src, IntPtr.Zero)) File.Copy(src, dst);
                }

                if (imageNiftiFull != null)
                {
                    // Preserve the source extension so the downstream writer sees the same
                    // file kind the user supplied (SimpleITK reads either form).
                    string stagedImage = Path.Combine(stage, Path.GetFileName(imageNiftiFull));
                    if (!CreateHardLink(stagedImage, imageNiftiFull, IntPtr.Zero))
                        File.Copy(imageNiftiFull, stagedImage);
                }

                // Optional --metadata override: copy the source metadata.json into the
                // staging folder so the metadata service picks it up.
                string metaPath = OptionalArg(args, "--metadata")
                    ?? Path.Combine(masksFolder, "metadata.json");
                if (File.Exists(metaPath))
                {
                    File.Copy(metaPath, Path.Combine(stage, "metadata.json"), overwrite: true);
                }

                var metaService = new NiftiMetadataService();
                var meta = metaService.LoadOrSynthesize(stage);

                // If image.nii.gz was staged, convert it now so the scanner can pick up
                // the resulting DICOM series as the reference.
                var imageWriter = new NiftiImageWriterService(metaService);
                imageWriter.ConvertImageNiftiToDicomSeries(stage, meta, progress, CancellationToken.None);

                Dicom_RT_images_Csharp.Models.DicomSeriesGroup imageSeries = null;
                var dcmFiles = Directory.EnumerateFiles(stage, "*.dcm", SearchOption.TopDirectoryOnly).ToList();
                if (dcmFiles.Count > 0)
                {
                    imageSeries = BuildImageSeriesFromFolder(stage);
                }

                Console.Error.WriteLine($"  Masks folder:   {masksFolder}");
                Console.Error.WriteLine($"  Image series:   {(imageSeries != null ? imageSeries.FilePaths.Count + " slice(s) (from image.nii.gz)" : "(none — metadata.json-only mode)")}");
                Console.Error.WriteLine($"  metadata.json:  {(File.Exists(metaPath) ? metaPath : "auto-generated in stage")}");
                Console.Error.WriteLine($"  Output:         {outputPath}");

                string written = writer.ConvertMasksFolderToRtStruct(
                    dicomFolder: stage,
                    referenceSeries: imageSeries,
                    outputPath: outputPath,
                    progress: progress,
                    ct: CancellationToken.None,
                    metadata: meta);

                // Persist the (possibly updated) metadata.json back next to the masks so
                // re-runs reuse the same UIDs.
                metaService.Save(masksFolder, meta);

                // Optionally persist the generated DICOM image series next to the RT-STRUCT
                // for inspection / comparison against an original reference series.
                string outImageFolder = OptionalArg(args, "--output-image-folder");
                if (!string.IsNullOrEmpty(outImageFolder) && imageSeries != null)
                {
                    Directory.CreateDirectory(outImageFolder);
                    foreach (var src in imageSeries.FilePaths)
                    {
                        string dst = Path.Combine(outImageFolder, Path.GetFileName(src));
                        File.Copy(src, dst, overwrite: true);
                    }
                    Console.Error.WriteLine($"  Copied {imageSeries.FilePaths.Count} DICOM image slice(s) to {outImageFolder}");
                }

                Console.Out.WriteLine("# rt_mask_validation reverse (nifti-only)");
                Console.Out.WriteLine(written);
                return 0;
            }
            finally
            {
                TryCleanupStaging(stage);
            }
        }

        // -----------------------------------------------------------------
        //  Image-reverse: NIfTI image volume -> DICOM image series
        // -----------------------------------------------------------------

        /// <summary>
        /// Standalone NIfTI -> DICOM image series conversion. Distinct from
        /// <see cref="RunReverseNiftiOnly"/> because no RT-STRUCT is written:
        /// the caller only wants the DICOM image series corresponding to the
        /// input NIfTI volume.
        /// </summary>
        private static int RunImageReverse(string[] args)
        {
            string niftiImage     = RequireArg(args, "--nifti-image");
            string outputFolder   = RequireArg(args, "--output-folder");
            string modality       = OptionalArg(args, "--modality") ?? "CT";
            string refDicomFolder = OptionalArg(args, "--ref-dicom-folder");

            if (!File.Exists(niftiImage))
                throw new FileNotFoundException($"NIfTI image not found: {niftiImage}");

            string upperMod = modality.ToUpperInvariant();
            if (upperMod != "CT" && upperMod != "MR" && upperMod != "PT")
                throw new ArgumentException(
                    $"Unsupported modality '{modality}'; expected CT, MR, or PT.");

            Directory.CreateDirectory(outputFolder);

            // Stage the input NIfTI as <stage>/image.nii.gz so the existing
            // NiftiImageWriterService -- which scans for that fixed filename --
            // runs unchanged. We then copy the generated DICOM files to the
            // caller's output folder.
            string stage = Path.Combine(
                Path.GetTempPath(),
                "rt_mask_validation_imrev_" + Path.GetRandomFileName());
            Directory.CreateDirectory(stage);
            try
            {
                string stagedImage = Path.Combine(stage, "image.nii.gz");
                if (!CreateHardLink(stagedImage, niftiImage, IntPtr.Zero))
                    File.Copy(niftiImage, stagedImage);

                var metaService = new NiftiMetadataService();
                var meta = metaService.LoadOrSynthesize(stage);
                meta.ImageModality = upperMod;

                if (!string.IsNullOrEmpty(refDicomFolder))
                {
                    Console.Error.WriteLine(
                        $"  (note) --ref-dicom-folder '{refDicomFolder}' currently ignored; "
                        + "metadata auto-synthesized.");
                }

                var progress = new Progress<string>(msg => Console.Error.WriteLine(msg));
                var imageWriter = new NiftiImageWriterService(metaService);
                var written = imageWriter.ConvertImageNiftiToDicomSeries(
                    stage, meta, progress, CancellationToken.None);

                if (written == null || written.Count == 0)
                {
                    Console.Error.WriteLine("Conversion produced no DICOM slices.");
                    return 1;
                }

                var outputPaths = new List<string>();
                foreach (var src in written)
                {
                    string dst = Path.Combine(outputFolder, Path.GetFileName(src));
                    File.Copy(src, dst, overwrite: true);
                    outputPaths.Add(dst);
                }

                Console.Error.WriteLine($"  NIfTI input:    {niftiImage}");
                Console.Error.WriteLine($"  Modality:       {upperMod}");
                Console.Error.WriteLine($"  Output folder:  {outputFolder}");
                Console.Error.WriteLine($"  Slices written: {outputPaths.Count}");

                Console.Out.WriteLine("# rt_mask_validation image-reverse");
                foreach (var p in outputPaths)
                    Console.Out.WriteLine(p);
                return 0;
            }
            finally
            {
                TryCleanupStaging(stage);
            }
        }

        // -----------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------

        private static DicomSeriesGroup BuildImageSeriesFromFolder(string folder)
        {
            // Filter to actual image slices: keep only files whose Modality is one of
            // the supported image modalities (CT/MR/PT) and that carry ImagePositionPatient.
            // This drops RT-STRUCT / RT-DOSE / RT-PLAN files that may live alongside the
            // image slices in the same folder -- ImageSeriesReader cannot ingest them
            // and would crash with a GDCM read error.
            var imageFiles = new List<string>();
            DicomDataset firstImage = null;
            foreach (var path in Directory.EnumerateFiles(folder, "*.dcm", SearchOption.TopDirectoryOnly)
                                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                DicomDataset ds;
                try { ds = DicomFile.Open(path, FileReadOption.SkipLargeTags).Dataset; }
                catch { continue; }

                string modality = GetStringOrEmpty(ds, DicomTag.Modality);
                if (string.Equals(modality, "RTSTRUCT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modality, "RTDOSE",   StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modality, "RTPLAN",   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!ds.Contains(DicomTag.ImagePositionPatient))
                {
                    continue;
                }

                imageFiles.Add(path);
                if (firstImage == null) firstImage = ds;
            }
            if (imageFiles.Count == 0 || firstImage == null)
                throw new InvalidOperationException($"No image (.dcm) slices in {folder}.");

            return new DicomSeriesGroup
            {
                SeriesInstanceUID    = GetStringOrEmpty(firstImage, DicomTag.SeriesInstanceUID),
                SeriesDescription    = GetStringOrEmpty(firstImage, DicomTag.SeriesDescription),
                Modality             = GetStringOrEmpty(firstImage, DicomTag.Modality),
                SeriesDate           = GetStringOrEmpty(firstImage, DicomTag.SeriesDate),
                FrameOfReferenceUID  = GetStringOrEmpty(firstImage, DicomTag.FrameOfReferenceUID),
                FilePaths            = imageFiles,
            };
        }

        private static DicomSeriesGroup BuildRtDoseSeriesFromFile(string rtdosePath)
        {
            var dcm = DicomFile.Open(rtdosePath).Dataset;
            return new DicomSeriesGroup
            {
                SeriesInstanceUID   = GetStringOrEmpty(dcm, DicomTag.SeriesInstanceUID),
                SeriesDescription   = GetStringOrEmpty(dcm, DicomTag.SeriesDescription),
                Modality            = GetStringOrEmpty(dcm, DicomTag.Modality),
                SeriesDate          = GetStringOrEmpty(dcm, DicomTag.SeriesDate),
                FrameOfReferenceUID = GetStringOrEmpty(dcm, DicomTag.FrameOfReferenceUID),
                FilePaths           = new List<string> { rtdosePath },
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
            foreach (var src in NiftiFileNaming.EnumerateNiftiFiles(masksFolder))
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
            string v = OptionalArg(args, name);
            if (v == null)
                throw new ArgumentException($"Required argument '{name}' is missing.");
            return v;
        }

        private static string OptionalArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
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
            Console.Error.WriteLine("                       [--include-image]      (also write image.nii.gz)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Reverse (per-ROI masks -> RTSTRUCT) with reference DICOM:");
            Console.Error.WriteLine("  --headless --reverse --image-folder PATH --masks-folder PATH --output PATH");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Reverse (per-ROI masks -> RTSTRUCT) without reference DICOM:");
            Console.Error.WriteLine("  --headless --reverse --masks-folder PATH --output PATH");
            Console.Error.WriteLine("                       [--image-nifti PATH]         (default: <masks-folder>/image.nii.gz)");
            Console.Error.WriteLine("                       [--metadata PATH]            (default: <masks-folder>/metadata.json)");
            Console.Error.WriteLine("                       [--output-image-folder PATH] (persist generated DICOM image series)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Image-reverse (NIfTI image volume -> DICOM image series):");
            Console.Error.WriteLine("  --headless --image-reverse --nifti-image PATH --output-folder PATH");
            Console.Error.WriteLine("                       [--modality CT|MR|PT]         (default: CT)");
            Console.Error.WriteLine("                       [--ref-dicom-folder PATH]     (currently ignored; metadata auto-synthesized)");
        }
    }
}
