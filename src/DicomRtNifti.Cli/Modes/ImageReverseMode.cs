using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DicomRtNifti.Core.Services;
using itk.simple;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --image-reverse: a NIfTI image volume -> a DICOM image series. stdout is
    /// "# rt_mask_validation image-reverse" then every slice written.
    /// </summary>
    internal sealed class ImageReverseMode : ICliMode
    {
        public string Flag => "--image-reverse";

        public int Order => 30;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Image-reverse (NIfTI image volume -> DICOM image series):",
            "  --headless --image-reverse --nifti-image PATH --output-folder PATH",
            "                       [--modality CT|MR|PT|auto]    (default: auto -- inferred from pixel values)",
            "                       [--ref-dicom-folder PATH]     (currently ignored; metadata auto-synthesized)",
        };

        public int Run(string[] args) => RunImageReverse(args);

        /// <summary>
        /// Standalone NIfTI -> DICOM image series conversion. Distinct from
        /// ReverseMode's nifti-only path because no RT-STRUCT is written:
        /// the caller only wants the DICOM image series corresponding to the
        /// input NIfTI volume.
        /// </summary>
        private static int RunImageReverse(string[] args)
        {
            string niftiImage     = CliArgs.RequireArg(args, "--nifti-image");
            string outputFolder   = CliArgs.RequireArg(args, "--output-folder");
            // Default to "auto": infer CT/MR/PT from the NIfTI's pixel values
            // unless the caller pins it explicitly. Keeps backward compat for
            // callers that pass --modality CT|MR|PT.
            string modality       = CliArgs.OptionalArg(args, "--modality") ?? "auto";
            string refDicomFolder = CliArgs.OptionalArg(args, "--ref-dicom-folder");

            if (!File.Exists(niftiImage))
                throw new CliUsageException($"NIfTI image not found: {niftiImage}");

            string upperMod = modality.ToUpperInvariant();
            if (upperMod != "CT" && upperMod != "MR" && upperMod != "PT"
                && upperMod != "AUTO")
            {
                throw new CliUsageException(
                    $"Unsupported modality '{modality}'; expected CT, MR, PT, or auto.");
            }

            // Auto-infer when requested. Cheap (samples up to 100K voxels) so
            // we just read the NIfTI here -- NiftiImageWriterService re-reads
            // it later via the staging folder; the duplicate read costs ~ms.
            if (upperMod == "AUTO")
            {
                using (var probeImage = SimpleITK.ReadImage(niftiImage))
                {
                    upperMod = NiftiModalityInferenceService.Infer(probeImage);
                }
                Console.Error.WriteLine($"  Modality auto-inferred as: {upperMod}");
            }

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
                HeadlessRunner.StageFile(niftiImage, stagedImage);

                var metaService = new NiftiMetadataService();
                var meta = metaService.LoadOrSynthesize(stage);
                meta.ImageModality = upperMod;

                if (!string.IsNullOrEmpty(refDicomFolder))
                {
                    Console.Error.WriteLine(
                        $"  (note) --ref-dicom-folder '{refDicomFolder}' currently ignored; "
                        + "metadata auto-synthesized.");
                }

                var progress = new StderrProgress();
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
                HeadlessRunner.TryCleanupStaging(stage);
            }
        }
    }
}
