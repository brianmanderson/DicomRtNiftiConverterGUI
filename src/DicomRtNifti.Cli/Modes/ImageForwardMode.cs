using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --image-forward: a DICOM image series -> a NIfTI image volume, no RTSTRUCT needed. stdout is
    /// "# rt_mask_validation image-forward" then the file written.
    /// </summary>
    internal sealed class ImageForwardMode : ICliMode
    {
        public string Flag => "--image-forward";

        public int Order => 40;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Image-forward (DICOM image series -> NIfTI image volume):",
            "  --headless --image-forward --image-folder PATH --output PATH",
            "                       [--target-spacing X,Y,Z]      (optional resample, mm)",
        };

        public int Run(string[] args) => RunImageForward(args);

        /// <summary>
        /// Standalone DICOM image series -> NIfTI conversion. Wraps the existing
        /// <see cref="NiftiConversionService.ConvertImageSeriesToNifti"/> service
        /// so callers can produce a clean NIfTI from a folder of DICOM image
        /// slices without supplying an RT-STRUCT (unlike <c>--forward
        /// --include-image</c>, which requires one).
        /// </summary>
        private static int RunImageForward(string[] args)
        {
            string imageFolder = CliArgs.RequireArg(args, "--image-folder");
            string outputPath  = CliArgs.RequireArg(args, "--output");
            string spacingArg  = CliArgs.OptionalArg(args, "--target-spacing");

            if (!Directory.Exists(imageFolder))
                throw new CliUsageException($"Image folder not found: {imageFolder}");

            double[] targetSpacing = null;
            if (!string.IsNullOrEmpty(spacingArg))
            {
                var parts = spacingArg.Split(',');
                if (parts.Length != 3)
                    throw new CliUsageException(
                        $"--target-spacing expects 'X,Y,Z'; got '{spacingArg}'.");
                targetSpacing = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (!double.TryParse(
                            parts[i].Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out targetSpacing[i]) || targetSpacing[i] <= 0)
                    {
                        throw new CliUsageException(
                            $"--target-spacing component '{parts[i]}' is not a positive number.");
                    }
                }
            }

            // ConvertImageSeriesToNifti writes to <outputDir>/image.nii.gz with a
            // hardcoded filename, so stage into a temp dir and move/rename to the
            // caller-specified path. This mirrors the --image-reverse pattern.
            string outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            string stage = Path.Combine(
                Path.GetTempPath(),
                "rt_mask_validation_imfwd_" + Path.GetRandomFileName());
            Directory.CreateDirectory(stage);
            try
            {
                var imageSeries = HeadlessRunner.BuildImageSeriesFromFolder(imageFolder);

                Console.Error.WriteLine($"  Image series: {imageSeries.FilePaths.Count} files in {imageFolder}");
                Console.Error.WriteLine($"  Modality:     {imageSeries.Modality}");
                if (targetSpacing != null)
                    Console.Error.WriteLine($"  Target spacing: {targetSpacing[0]}x{targetSpacing[1]}x{targetSpacing[2]} mm");
                Console.Error.WriteLine($"  Output:       {outputPath}");
                HeadlessRunner.WarnIfSliceSpacingNonUniform(imageSeries, targetSpacing);

                var maskService = new RtStructMaskService();
                var conversionService = new NiftiConversionService(maskService);
                var progress = new StderrProgress();

                conversionService.ConvertImageSeriesToNifti(
                    series: imageSeries,
                    outputDir: stage,
                    progress: progress,
                    ct: CancellationToken.None,
                    targetSpacing: targetSpacing);

                string staged = Path.Combine(stage, "image.nii.gz");
                if (!File.Exists(staged))
                {
                    Console.Error.WriteLine($"Conversion produced no NIfTI at {staged}.");
                    return 1;
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(staged, outputPath);

                Console.Out.WriteLine("# rt_mask_validation image-forward");
                Console.Out.WriteLine(Path.GetFullPath(outputPath));
                return 0;
            }
            finally
            {
                HeadlessRunner.TryCleanupStaging(stage);
            }
        }
    }
}
