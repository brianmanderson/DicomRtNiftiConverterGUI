using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --forward: RTSTRUCT + image series -> per-ROI binary masks (optionally image.nii.gz and
    /// doses/ too). stdout is "# rt_mask_validation forward" then one row per ROI:
    /// &lt;name&gt;\t&lt;volume_cc&gt;\t&lt;path&gt;. That contract is frozen: the benchmark harness and
    /// the analytic gate parse it.
    /// </summary>
    internal sealed class ForwardMode : ICliMode
    {
        public string Flag => "--forward";

        public int Order => 10;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Forward (RTSTRUCT -> per-ROI masks):",
            "  --headless --forward --rtstruct PATH --image-folder PATH --output-folder PATH",
            "                       [--include-image]      (also write image.nii.gz)",
            "                       [--rtdose PATH]        (also write doses/<description>.nii.gz)",
            "                       [--strict-reference]   (exit 1 instead of warning when the RTSTRUCT",
            "                                               does not reference this image series)",
        };

        public int Run(string[] args) => RunForward(args);

        private static int RunForward(string[] args)
        {
            string rtstructPath  = CliArgs.RequireArg(args, "--rtstruct");
            string imageFolder   = CliArgs.RequireArg(args, "--image-folder");
            string outputFolder  = CliArgs.RequireArg(args, "--output-folder");

            if (!File.Exists(rtstructPath))
                throw new CliUsageException($"RTSTRUCT not found: {rtstructPath}");
            if (!Directory.Exists(imageFolder))
                throw new CliUsageException($"Image folder not found: {imageFolder}");

            Directory.CreateDirectory(outputFolder);

            // Build the two DicomSeriesGroup objects the conversion service expects.
            // We don't need the full scanner -- the inputs are explicit.
            var imageSeries = HeadlessRunner.BuildImageSeriesFromFolder(imageFolder);
            var rtStructSeries = BuildRtStructSeriesFromFile(rtstructPath, out var rtReferences);

            Console.Error.WriteLine($"  Image series: {imageSeries.FilePaths.Count} files in {imageFolder}");
            Console.Error.WriteLine($"  RTSTRUCT:    {rtstructPath} ({rtStructSeries.RoiNames.Count} ROIs)");
            Console.Error.WriteLine($"  Output:      {outputFolder}");
            HeadlessRunner.WarnIfSliceSpacingNonUniform(imageSeries);

            // The inputs are explicit, so nothing links the structure set to the series for us: a
            // caller can hand this route an RTSTRUCT drawn on a different scan and the contours
            // would be rasterized onto the wrong grid at exit 0. Check what the RTSTRUCT itself
            // says it references and say so; --strict-reference turns the warning into a refusal.
            string referenceMismatch = HeadlessRunner.DescribeReferenceMismatch(
                rtReferences.SeriesUids, rtReferences.FrameOfReferenceUids, imageSeries);
            if (referenceMismatch != null)
            {
                if (CliArgs.HasFlag(args, "--strict-reference"))
                {
                    Console.Error.WriteLine($"ERROR: {referenceMismatch}. Refusing to rasterize under --strict-reference.");
                    return 1;
                }
                Console.Error.WriteLine(
                    $"  WARNING: {referenceMismatch}. The contours are rasterized onto this image " +
                    "series anyway; pass --strict-reference to make this an error.");
            }

            var maskService       = new RtStructMaskService();
            var conversionService = new NiftiConversionService(maskService);

            var progress = new StderrProgress();

            if (CliArgs.HasFlag(args, "--include-image"))
            {
                conversionService.ConvertImageSeriesToNifti(
                    series: imageSeries,
                    outputDir: outputFolder,
                    progress: progress,
                    ct: CancellationToken.None,
                    targetSpacing: null);
            }

            string rtdosePath = CliArgs.OptionalArg(args, "--rtdose");
            if (!string.IsNullOrEmpty(rtdosePath))
            {
                if (!File.Exists(rtdosePath))
                    throw new CliUsageException($"RT-DOSE file not found: {rtdosePath}");
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
            // The file names come from the same order-independent helper the writer used, so ROIs
            // whose names sanitize to the same string are reported at the paths they were actually
            // written to rather than all three claiming one file.
            var maskFileNames = NiftiConversionService.BuildUniqueMaskFileNames(roiVolumes.Keys);
            Console.Out.WriteLine("# rt_mask_validation forward");
            foreach (var kvp in roiVolumes)
            {
                string maskPath = Path.Combine(outputFolder, maskFileNames[kvp.Key] + ".nii.gz");
                Console.Out.WriteLine($"{kvp.Key}\t{kvp.Value:G}\t{maskPath}");
            }
            return 0;
        }

        private static DicomSeriesGroup BuildRtDoseSeriesFromFile(string rtdosePath)
        {
            var dcm = DicomFile.Open(rtdosePath).Dataset;
            return new DicomSeriesGroup
            {
                SeriesInstanceUID   = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesInstanceUID),
                SeriesDescription   = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesDescription),
                Modality            = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.Modality),
                SeriesDate          = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesDate),
                FrameOfReferenceUID = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.FrameOfReferenceUID),
                FilePaths           = new List<string> { rtdosePath },
            };
        }

        /// <summary>Everything a structure set says it was drawn on.</summary>
        internal sealed class RtStructReferences
        {
            public List<string> SeriesUids = new List<string>();
            public List<string> FrameOfReferenceUids = new List<string>();
        }

        private static DicomSeriesGroup BuildRtStructSeriesFromFile(string rtstructPath, out RtStructReferences references)
        {
            var dcm = DicomFile.Open(rtstructPath).Dataset;
            references = new RtStructReferences
            {
                SeriesUids = DicomScannerService.ExtractReferencedSeriesUIDs(dcm),
                FrameOfReferenceUids = DicomScannerService.ExtractRtStructFrameOfReferenceUids(dcm),
            };
            var series = new DicomSeriesGroup
            {
                SeriesInstanceUID   = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesInstanceUID),
                SeriesDescription   = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesDescription),
                Modality            = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.Modality),
                SeriesDate          = HeadlessRunner.GetStringOrEmpty(dcm, DicomTag.SeriesDate),
                FrameOfReferenceUID = references.FrameOfReferenceUids.FirstOrDefault() ?? "",
                ReferencedSeriesUID = references.SeriesUids.FirstOrDefault() ?? "",
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
    }
}
