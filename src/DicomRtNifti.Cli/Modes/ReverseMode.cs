using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --reverse: per-ROI binary masks -> RTSTRUCT, against a reference DICOM series or, without
    /// one, from the masks folder's metadata.json (and image.nii.gz when present). stdout is
    /// "# rt_mask_validation reverse" (or "reverse (nifti-only)") then the RTSTRUCT written.
    /// </summary>
    internal sealed class ReverseMode : ICliMode
    {
        public string Flag => "--reverse";

        public int Order => 20;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Reverse (per-ROI masks -> RTSTRUCT) with reference DICOM:",
            "  --headless --reverse --image-folder PATH --masks-folder PATH --output PATH",
            "",
            "Reverse (per-ROI masks -> RTSTRUCT) without reference DICOM:",
            "  --headless --reverse --masks-folder PATH --output PATH",
            "                       [--image-nifti PATH]         (default: <masks-folder>/image.nii.gz)",
            "                       [--metadata PATH]            (default: <masks-folder>/metadata.json)",
            "                       [--output-image-folder PATH] (persist generated DICOM image series)",
        };

        public int Run(string[] args) => RunReverse(args);

        private static int RunReverse(string[] args)
        {
            string imageFolder = CliArgs.OptionalArg(args, "--image-folder");
            string masksFolder = CliArgs.RequireArg(args, "--masks-folder");
            string outputPath  = CliArgs.RequireArg(args, "--output");

            if (!Directory.Exists(masksFolder))
                throw new CliUsageException($"Masks folder not found: {masksFolder}");
            if (!string.IsNullOrEmpty(imageFolder) && !Directory.Exists(imageFolder))
                throw new CliUsageException($"Image folder not found: {imageFolder}");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));

            var writer = new RtStructWriterService();
            var progress = new StderrProgress();

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
            string stagingFolder = HeadlessRunner.CreateMasksStagingFolder(imageFolder, masksFolder);

            try
            {
                var imageSeries = HeadlessRunner.BuildImageSeriesFromFolder(imageFolder);

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
                HeadlessRunner.TryCleanupStaging(stagingFolder);
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
                string imageNiftiArg = CliArgs.OptionalArg(args, "--image-nifti");
                string imageNifti;
                if (!string.IsNullOrEmpty(imageNiftiArg))
                    imageNifti = imageNiftiArg;
                else if (!NiftiFileNaming.TryGetImageNiftiPath(masksFolder, out imageNifti))
                    imageNifti = Path.Combine(masksFolder, NiftiFileNaming.ImageNiiGz);
                string imageNiftiFull = File.Exists(imageNifti) ? Path.GetFullPath(imageNifti) : null;

                var maskSources = new List<string>();
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
                    maskSources.Add(src);
                }
                HeadlessRunner.StageMasks(stagedMasks, maskSources);

                if (imageNiftiFull != null)
                {
                    // Preserve the source extension so the downstream writer sees the same
                    // file kind the user supplied (SimpleITK reads either form).
                    string stagedImage = Path.Combine(stage, Path.GetFileName(imageNiftiFull));
                    HeadlessRunner.StageFile(imageNiftiFull, stagedImage);
                }

                // Optional --metadata override: copy the source metadata.json into the
                // staging folder so the metadata service picks it up.
                string metaPath = CliArgs.OptionalArg(args, "--metadata")
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

                DicomRtNifti.Core.Models.DicomSeriesGroup imageSeries = null;
                var dcmFiles = Directory.EnumerateFiles(stage, "*.dcm", SearchOption.TopDirectoryOnly).ToList();
                if (dcmFiles.Count > 0)
                {
                    imageSeries = HeadlessRunner.BuildImageSeriesFromFolder(stage);
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
                string outImageFolder = CliArgs.OptionalArg(args, "--output-image-folder");
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
                HeadlessRunner.TryCleanupStaging(stage);
            }
        }
    }
}
