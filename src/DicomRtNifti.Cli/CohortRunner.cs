using System;
using System.IO;
using System.Threading;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Cli
{
    /// <summary>
    /// Cohort-level headless modes: scan a DICOM tree, survey it into a manifest, or convert it
    /// wholesale. These reuse <see cref="CohortExportService"/>, so a scripted run and the
    /// desktop app's Convert Selected produce the same output.
    ///
    /// Output contract, deliberately different from the single-series modes:
    ///   * stdout carries exactly one JSON document and nothing else, so a caller can pipe it
    ///     straight into a parser. The "# rt_mask_validation" header the older modes emit would
    ///     make that fail, so it is not used here.
    ///   * stderr carries all human-readable progress.
    ///   * The documents themselves are built in Core (<see cref="CohortDocuments"/>), so every
    ///     front-end prints the same bytes; this class prints them with absolute paths as they
    ///     are. Under --anonymize the key is written outside the output root (beside it by
    ///     default, or at --key-path) and its full path is anonymization_key. Notebooks get
    ///     committed, so their cell output must not carry PHI.
    ///   * The command line is parsed by Core's grammar (<see cref="CohortCommandLine"/>); what is
    ///     left here is the file-system checks, the stderr progress and the exit codes.
    /// Exit codes match the other modes: 0 ok, 1 conversion failure, 2 bad arguments.
    ///
    /// Each mode's entry point is its file under Modes/; this class is what they share.
    /// </summary>
    internal static class CohortRunner
    {
        // -----------------------------------------------------------------------------------
        //  Shared by the three cohort modes (Modes/Cohort*Mode.cs)
        // -----------------------------------------------------------------------------------

        internal static (CohortExportResult, CohortExportPlan) RunCohort(
            CohortExportOptions options, bool convert)
        {
            if (!Directory.Exists(options.InputRoot))
                throw new CliUsageException($"Input folder not found: {options.InputRoot}");

            // Load the key before the scan, not after. It is the one input that can refuse the run
            // outright — an unreadable key, or one recorded under a different salt — and refusing
            // after a full tree walk means the operator waits out a scan of the whole cohort to be
            // told the run was never going to start.
            // The key goes beside the export (or to --key-path), never inside it (#54); a key an
            // earlier run left inside the root is merged in and reported, not ignored.
            AnonymizationService anon = null;
            if (options.Anonymize)
            {
                string legacyKeyWarning;
                try
                {
                    anon = AnonymizationKeyLocation.Open(
                        options.OutputRoot, options.KeyPath, options.Salt, out legacyKeyWarning);
                }
                catch (ArgumentException ex)
                {
                    throw new CliUsageException(ex.Message);
                }
                if (legacyKeyWarning != null)
                    Console.Error.WriteLine("  WARNING: " + legacyKeyWarning);
                Console.Error.WriteLine($"  Anonymization key: {anon.KeyFilePath}");
            }

            var scan = ScanTree(options.InputRoot);

            var plan = CohortExportService.BuildPlan(scan, options, anon);
            Console.Error.WriteLine(
                $"  Planned {plan.Series.Count} series ({plan.Skipped.Count} skipped, "
                + $"{plan.UnlinkedRtObjects.Count} unlinked RT objects)");

            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));
            var progress = new StderrProgress("  ");

            var result = convert
                ? service.ExecuteAsync(plan, options, anon, progress, CancellationToken.None)
                    .GetAwaiter().GetResult()
                : service.ComputeManifestAsync(plan, options, anon, progress, CancellationToken.None)
                    .GetAwaiter().GetResult();

            return (result, plan);
        }

        // -----------------------------------------------------------------------------------
        //  --dry-run
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Scans and plans exactly as the run would, prints the plan, and writes nothing: no
        /// output folder, no manifest, no key. Under --anonymize the key is read where the run
        /// would write it, so manual overrides show, and never saved. --json-out is still honoured,
        /// as long as it does not lie inside the output folder the dry run must not create.
        /// </summary>
        internal static int RunPlan(CohortCommand command)
        {
            var options = command.Options;
            if (!Directory.Exists(options.InputRoot))
                throw new CliUsageException($"Input folder not found: {options.InputRoot}");
            if (!string.IsNullOrEmpty(command.JsonOutPath)
                && AnonymizationKeyLocation.IsWithin(command.JsonOutPath, options.OutputRoot))
            {
                throw new CliUsageException(
                    $"--json-out '{command.JsonOutPath}' lies inside --output '{options.OutputRoot}', which "
                    + "a dry run does not create. Write the plan somewhere else, or leave --json-out off.");
            }

            AnonymizationService anon = null;
            string keyFile = null;
            if (options.Anonymize)
            {
                string legacyKeyWarning;
                try
                {
                    anon = ReadOnlyAnonymizationKey.OpenForExport(
                        options.OutputRoot, options.KeyPath, options.Salt, out keyFile, out legacyKeyWarning);
                }
                catch (ArgumentException ex)
                {
                    throw new CliUsageException(ex.Message);
                }
                if (legacyKeyWarning != null)
                    Console.Error.WriteLine("  WARNING: " + legacyKeyWarning);
                Console.Error.WriteLine($"  Anonymization key: {keyFile} (read only)");
            }

            var scan = ScanTree(options.InputRoot);

            var plan = CohortExportService.BuildPlan(scan, options, anon);
            Console.Error.WriteLine(
                $"  Planned {plan.Series.Count} series ({plan.Skipped.Count} skipped, "
                + $"{plan.UnlinkedRtObjects.Count} unlinked RT objects)");
            Console.Error.WriteLine("  Dry run: nothing written.");

            Emit(command, CohortPlanDocument.Build(
                command.Mode, plan, options, anon, keyFile, CohortDocuments.Identity));
            return 0;
        }

        // -----------------------------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------------------------

        internal static DicomScanResult ScanTree(string input)
        {
            Console.Error.WriteLine($"  Scanning {Path.GetFullPath(input)} ...");
            var progress = new StderrProgress("  ");
            var scan = new DicomScannerService()
                .ScanFolderAsync(input, progress, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (scan.SkippedErrorCount > 0)
            {
                Console.Error.WriteLine(
                    $"  WARNING: {scan.SkippedErrorCount} file(s) could not be read; see unreadable_samples.");
            }
            return scan;
        }

        /// <summary>Writes the document to stdout, and to --json-out as well when given.</summary>
        internal static void Emit(CohortCommand command, object document)
        {
            string json = CohortDocuments.Serialize(document);
            Console.Out.WriteLine(json);

            string jsonOut = command.JsonOutPath;
            if (!string.IsNullOrEmpty(jsonOut))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(jsonOut));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(jsonOut, json);
                Console.Error.WriteLine($"  Wrote {jsonOut}");
            }
        }
    }

    /// <summary>The parts of the cohort section of --help that belong to no single cohort mode.</summary>
    internal static class CohortUsage
    {
        /// <summary>The line that opens the cohort section of --help, before the first cohort mode.</summary>
        public static readonly string[] Preamble =
        {
            "Cohort modes scan a tree recursively and emit one JSON document on stdout.",
        };

        /// <summary>
        /// The options the survey and the conversion share, then the conversion's own: printed
        /// after the last cohort mode, since they describe both.
        /// </summary>
        public static readonly string[] SharedOptions =
        {
            "Options common to --cohort-manifest and --cohort-convert:",
            "  --associations FILE.json          canonical-name / alias mappings",
            "  --only-associated-rois            drop ROIs that match no association",
            "                                    (warns without --associations; exit 1 if nothing matches)",
            "  --output-spacing X,Y,Z            resample to a fixed grid, mm",
            "                                    (--target-spacing is accepted as an alias)",
            "  --anonymize [--salt STRING]       hash identifiers; writes AnonymizationKey.json outside",
            "                                    the output, in <output>.key/ beside it by default",
            "  --key-path FILE|FOLDER            with --anonymize: where to write the key instead",
            "                                    (refused inside --output)",
            "  --patients ID,ID                  restrict to these PatientIDs",
            "  --series-description SUBSTR       keep image series whose description matches",
            "  --struct-description SUBSTR       keep image series whose linked RTSTRUCT",
            "                                    description matches, and export that one",
            "  --prefer-largest-series           keep only the largest image series per study",
            "                                    (equal slice counts break on SeriesInstanceUID, so",
            "                                    repeated runs pick the same series; prefer the",
            "                                    filters above when the choice matters)",
            "  --require-structures              skip series with no linked RTSTRUCT",
            "  --require-dose                    skip series with no linked RTDOSE",
            "  --fail-fast                       abort on the first failing series",
            "  --manifest-name NAME              default: export_manifest.csv",
            "  --json-out PATH                   also write the JSON document to a file",
            "",
            "Options for --cohort-convert only:",
            "  --metadata-tags KW,...            metadata.json image section",
            "  --metadata-structure-tags KW,...  metadata.json structure section",
            "  --metadata-dose-tags KW,...       metadata.json dose section",
            "      Keywords are fo-dicom names (PatientAge, KVP, DoseUnits) plus computed",
            "      values (@VoxelSize, @RoiNames, @MaxDose, @DoseVoxelSize).",
            "  --no-images | --no-structures | --no-doses   skip that output",
        };
    }
}
