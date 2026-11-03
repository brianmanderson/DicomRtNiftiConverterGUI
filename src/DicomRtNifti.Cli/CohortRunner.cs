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
    /// </summary>
    internal static class CohortRunner
    {
        // -----------------------------------------------------------------------------------
        //  --cohort-scan
        // -----------------------------------------------------------------------------------

        public static int RunScan(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Scan, args, new StderrProgress("  "));
            string input = command.Options.InputRoot;
            if (!Directory.Exists(input))
                throw new CliUsageException($"Input folder not found: {input}");

            var scan = ScanTree(input);

            Emit(command, CohortScanDocument.Build(scan, input, CohortDocuments.Identity));
            return 0;
        }

        // -----------------------------------------------------------------------------------
        //  --cohort-manifest / --cohort-convert
        // -----------------------------------------------------------------------------------

        public static int RunManifest(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Manifest, args, new StderrProgress("  "));
            var options = command.Options;
            var (result, plan) = RunCohort(options, convert: false);

            Emit(command, CohortManifestDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        public static int RunConvert(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Convert, args, new StderrProgress("  "));
            var options = command.Options;
            var (result, plan) = RunCohort(options, convert: true);

            Emit(command, CohortConvertDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        private static (CohortExportResult, CohortExportPlan) RunCohort(
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
        //  Helpers
        // -----------------------------------------------------------------------------------

        private static DicomScanResult ScanTree(string input)
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
        private static void Emit(CohortCommand command, object document)
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
}
