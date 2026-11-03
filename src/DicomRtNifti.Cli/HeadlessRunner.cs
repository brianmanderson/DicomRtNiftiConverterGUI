using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DicomRtNifti.Cli.Modes;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;

namespace DicomRtNifti.Cli
{
    /// <summary>
    /// Headless command-line entry point for benchmark / batch use, invoked from
    /// <c>Program.Main</c>. Reuses the shared services so behaviour is identical to the
    /// GUI's Convert Selected / NIfTI-to-DICOM workflows.
    ///
    /// Each mode is one file under Modes/ (<see cref="Modes.ICliMode"/>) declaring its flag, its
    /// --help block and its entry point; this class handles --help and --version, dispatches to
    /// the discovered modes in order, maps usage errors to exit 2, and holds the helpers several
    /// modes share.
    ///
    /// stdout: machine-readable lines describing every output file written (one JSON document for
    /// the cohort modes).
    /// stderr: human-readable progress / error messages.
    /// Exit code: 0 on success; 2 when the command line cannot be acted on (no or unknown mode,
    /// a required flag missing, an input path that does not exist); 1 when the conversion itself
    /// fails, or when a check the caller asked to be fatal (--strict-reference) fails.
    /// </summary>
    public static class HeadlessRunner
    {
        /// <summary>
        /// Parse <paramref name="args"/> and dispatch to the requested headless command.
        /// </summary>
        public static int Run(string[] args)
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return 0;
            }
            if (HasFlag(args, "--version"))
            {
                Console.Out.WriteLine(GetVersionString());
                Console.Out.WriteLine(SimpleItkNative.Probe());
                return 0;
            }
            if (args == null || args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            try
            {
                // Every mode in its file under Modes/, tried in order; the first whose flag is
                // present runs. See ICliMode.Order for why the order is part of the contract.
                foreach (var mode in CliModes.All)
                {
                    if (HasFlag(args, mode.Flag))
                        return mode.Run(args);
                }

                Console.Error.WriteLine($"Headless mode requires {CliModes.DescribeFlags()}.");
                PrintUsage();
                return 2;
            }
            catch (CommandLineUsageException ex)
            {
                return UsageError(ex.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        /// <summary>
        /// Exit 2: the command line could not be acted on. A missing or malformed argument and an
        /// input path that does not exist are the caller's to fix, so they are reported without a
        /// stack trace and kept apart from exit 1, which means the conversion itself failed. Only
        /// argument checks raise <see cref="CommandLineUsageException"/> (the CLI's own as
        /// <see cref="CliUsageException"/>, and Core's cohort grammar); every exception from inside
        /// a conversion keeps exit 1 and its stack trace, whatever its type.
        /// </summary>
        private static int UsageError(string message)
        {
            Console.Error.WriteLine($"ERROR: {message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        // -----------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// The image series the explicit modes convert: every CT/MR/PT slice in the folder that
        /// can be placed along the slice axis. Public so the tests can hold it against the
        /// scanner's series for the same folder.
        /// </summary>
        public static DicomSeriesGroup BuildImageSeriesFromFolder(string folder)
        {
            // Filter to actual image slices: keep only files whose Modality is one of
            // the supported image modalities (CT/MR/PT) and that carry ImagePositionPatient.
            // This drops RT-STRUCT / RT-DOSE / RT-PLAN files that may live alongside the
            // image slices in the same folder -- ImageSeriesReader cannot ingest them
            // and would crash with a GDCM read error.
            var imageFiles = new List<string>();
            // Per-slice geometry captured while each header is already open, so
            // SeriesGeometryProbe can judge slice-spacing uniformity without a second pass.
            // DicomScannerService fills the same three fields for the cohort modes.
            var slicePositions = new List<double>();
            var unpositioned = new List<string>();
            double[] pixelSpacing = null;
            double? sliceThickness = null;
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
                // A slice that cannot be placed along the slice axis is left out and named (the
                // scanner applies the same rule), never kept and sorted to z = 0. Files of other
                // non-image modalities (SR, REG, ...) are skipped silently as they always were.
                double sliceZ;
                string why;
                bool positioned = SeriesGeometryProbe.TryReadSliceZ(ds, out sliceZ, out why);
                if (!positioned && !SeriesGeometryProbe.IsMultiFrame(ds))
                {
                    if (DicomScannerService.ImageModalities.Contains(modality))
                        unpositioned.Add(SeriesGeometryProbe.DescribeUnpositionedSlice(path, ds, why));
                    continue;
                }

                // A multi-frame object carries its positions per frame, not at the top level; it
                // is kept whole (the reader handles it) and contributes no slice position here.
                imageFiles.Add(path);
                if (firstImage == null) firstImage = ds;
                if (positioned)
                    slicePositions.Add(sliceZ);

                if (pixelSpacing == null && ds.Contains(DicomTag.PixelSpacing))
                {
                    try
                    {
                        var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                        if (ps != null && ps.Length >= 2)
                            pixelSpacing = new[] { ps[0], ps[1] };
                    }
                    catch { /* leave null; the probe just reports no in-plane spacing */ }
                }

                if (sliceThickness == null && ds.Contains(DicomTag.SliceThickness))
                {
                    try { sliceThickness = ds.GetSingleValue<double>(DicomTag.SliceThickness); }
                    catch { /* optional; only the single-slice fallback uses it */ }
                }
            }
            if (imageFiles.Count == 0 || firstImage == null)
            {
                string detail = unpositioned.Count == 0
                    ? ""
                    : $" {unpositioned.Count} image slice(s) had no usable ImagePositionPatient: " +
                      string.Join("; ", unpositioned) + ".";
                throw new InvalidOperationException($"No image (.dcm) slices in {folder}.{detail}");
            }

            return new DicomSeriesGroup
            {
                SeriesInstanceUID    = GetStringOrEmpty(firstImage, DicomTag.SeriesInstanceUID),
                SeriesDescription    = GetStringOrEmpty(firstImage, DicomTag.SeriesDescription),
                Modality             = GetStringOrEmpty(firstImage, DicomTag.Modality),
                SeriesDate           = GetStringOrEmpty(firstImage, DicomTag.SeriesDate),
                FrameOfReferenceUID  = GetStringOrEmpty(firstImage, DicomTag.FrameOfReferenceUID),
                FilePaths            = imageFiles,
                SlicePositions       = slicePositions,
                UnpositionedSlices   = unpositioned,
                PixelSpacing         = pixelSpacing,
                SliceThickness       = sliceThickness,
            };
        }

        /// <summary>
        /// Emits SeriesGeometryProbe's non-uniform-spacing warning on stderr, if there is one.
        ///
        /// The probe existed but only the cohort-scan mode ever consulted it, so a mixed-gap
        /// series went through --forward / --image-forward with exit 0 and no message naming the
        /// spacing it had just been flattened to. Deliberately non-fatal: the conversion is still
        /// the best available answer, and the geometry written is left exactly as it was — the
        /// caller just gets told what it is.
        ///
        /// <paramref name="targetSpacing"/> only changes the wording: under --target-spacing the
        /// flattened spacing is what the series is resampled *from*, not what is written.
        /// </summary>
        internal static void WarnIfSliceSpacingNonUniform(
            DicomSeriesGroup imageSeries, double[] targetSpacing = null)
        {
            // Skipped slices first: the spacing warning that follows judges the reduced series,
            // and a hole where a skipped slice was is what it would otherwise be reporting.
            string missing;
            if (SeriesGeometryProbe.TryBuildMissingPositionWarning(imageSeries, out missing))
                Console.Error.WriteLine("  " + missing);

            string warning;
            if (SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(imageSeries, targetSpacing, out warning))
                Console.Error.WriteLine("  " + warning);
        }

        /// <summary>
        /// Explains why a structure set does not belong to <paramref name="image"/>, or returns
        /// null when it does (or when the structure set says nothing checkable). A structure set
        /// may reference several image series and frames (a planning CT plus a registered MR, for
        /// one); it belongs to <paramref name="image"/> when the image's series is any of the
        /// referenced series. Only when it names no series is the frame of reference, the weaker
        /// test, consulted: the image's frame must then be one of the structure set's frames.
        /// </summary>
        public static string DescribeReferenceMismatch(
            IReadOnlyCollection<string> referencedSeriesUids,
            IReadOnlyCollection<string> frameOfReferenceUids,
            DicomSeriesGroup image)
        {
            if (image == null)
                return null;

            var series = (referencedSeriesUids ?? new string[0]).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (series.Count > 0 && !string.IsNullOrEmpty(image.SeriesInstanceUID))
            {
                // Authoritative either way: a referenced series settles it even when the frame
                // looks wrong, exactly as the scanner's linking takes the series UID first.
                if (series.Contains(image.SeriesInstanceUID, StringComparer.Ordinal))
                    return null;
                return $"the RTSTRUCT references image series {string.Join(", ", series)} " +
                       $"(RTReferencedSeriesSequence), but --image-folder holds series {image.SeriesInstanceUID}";
            }

            var frames = (frameOfReferenceUids ?? new string[0]).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (frames.Count > 0 && !string.IsNullOrEmpty(image.FrameOfReferenceUID) &&
                !frames.Contains(image.FrameOfReferenceUID, StringComparer.Ordinal))
            {
                return $"the RTSTRUCT's frame of reference {string.Join(", ", frames)} is not the " +
                       $"image series' {image.FrameOfReferenceUID}";
            }

            return null;
        }

        /// <summary>Single-identifier form, as a scanned <see cref="DicomSeriesGroup"/> carries them.</summary>
        public static string DescribeReferenceMismatch(DicomSeriesGroup rtStruct, DicomSeriesGroup image)
        {
            if (rtStruct == null)
                return null;
            return DescribeReferenceMismatch(
                new[] { rtStruct.ReferencedSeriesUID }, new[] { rtStruct.FrameOfReferenceUID }, image);
        }

        /// <summary>
        /// Create a temporary directory ``stage/`` next to ``imageFolder`` containing:
        ///   * symbolic links / copies of every .dcm in ``imageFolder``
        ///   * a ``masks/`` subdirectory with every .nii.gz from ``masksFolder``
        /// so that <see cref="RtStructWriterService"/> -- which expects the masks at
        /// ``&lt;dicomFolder&gt;/masks/`` -- can run unchanged.
        /// </summary>
        internal static string CreateMasksStagingFolder(string imageFolder, string masksFolder)
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
                StageFile(src, dst);
            }
            StageMasks(masksDir, NiftiFileNaming.EnumerateNiftiFiles(masksFolder));
            return stage;
        }

        /// <summary>
        /// Copies every mask into the staging folder under a name that is guaranteed to fit the
        /// path limit. See <see cref="MaskStagingNames"/>: the staged path is the one that has to
        /// fit, and an over-long one used to cost the ROI silently at exit 0.
        /// </summary>
        internal static void StageMasks(string stagedMasksDir, IEnumerable<string> sources)
        {
            var notices = new List<string>();
            var stagedNames = MaskStagingNames.BuildStagedFileNames(stagedMasksDir, sources, notices);

            foreach (var notice in notices)
                Console.Error.WriteLine("  " + notice);

            foreach (var entry in stagedNames)
                StageFile(entry.Key, Path.Combine(stagedMasksDir, entry.Value));
        }

        /// <summary>
        /// Stage an input file into a temporary working directory. The legacy code
        /// hard-linked via the Win32 CreateHardLink API; a plain copy is cross-platform and
        /// the staging directories are short-lived (created with random names, then deleted).
        /// </summary>
        internal static void StageFile(string src, string dst)
        {
            File.Copy(src, dst, overwrite: true);
        }

        internal static void TryCleanupStaging(string stage)
        {
            try { Directory.Delete(stage, recursive: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (warn) failed to clean staging '{stage}': {ex.Message}");
            }
        }

        internal static string GetStringOrEmpty(DicomDataset ds, DicomTag tag)
        {
            try { return ds.GetSingleValueOrDefault(tag, ""); }
            catch { return ""; }
        }

        private static bool HasFlag(string[] args, string flag) => CliArgs.HasFlag(args, flag);

        private static string SanitizeFileName(string name)
        {
            return WindowsPathSanitizer.SanitizeName(name);
        }

        private static string GetVersionString()
        {
            var asm = typeof(HeadlessRunner).Assembly;
            string ver =
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
            return $"DicomRtNifti CLI {ver}";
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine(GetVersionString());
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage: DicomRtNifti.Cli [--headless] <mode> [options]");
            Console.Error.WriteLine("  (the leading --headless flag is optional, accepted for backward compatibility)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Global:");
            Console.Error.WriteLine("  --help, -h    Show this help and exit");
            Console.Error.WriteLine("  --version     Show version + SimpleITK native status and exit");
            Console.Error.WriteLine();
            for (int i = 0; i < CliModes.All.Count; i++)
            {
                if (i > 0)
                    Console.Error.WriteLine();
                foreach (var line in CliModes.All[i].Usage)
                    Console.Error.WriteLine(line);
            }
        }
    }
}
