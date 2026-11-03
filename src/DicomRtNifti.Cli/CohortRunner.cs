using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// Exit codes match the other modes: 0 ok, 1 conversion failure, 2 bad arguments.
    /// </summary>
    internal static class CohortRunner
    {
        // -----------------------------------------------------------------------------------
        //  --cohort-scan
        // -----------------------------------------------------------------------------------

        public static int RunScan(string[] args)
        {
            string input = CliArgs.RequireArg(args, "--input");
            if (!Directory.Exists(input))
                throw new CliUsageException($"Input folder not found: {input}");

            var scan = ScanTree(input);

            var document = CohortScanDocument.Build(scan, input, CohortDocuments.Identity);

            Emit(args, document);
            return 0;
        }

        // -----------------------------------------------------------------------------------
        //  --cohort-manifest / --cohort-convert
        // -----------------------------------------------------------------------------------

        public static int RunManifest(string[] args)
        {
            var options = BuildOptions(args, forConvert: false);
            var (result, plan) = RunCohort(args, options, convert: false);

            Emit(args, CohortManifestDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        public static int RunConvert(string[] args)
        {
            var options = BuildOptions(args, forConvert: true);
            var (result, plan) = RunCohort(args, options, convert: true);

            Emit(args, CohortConvertDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        private static (CohortExportResult, CohortExportPlan) RunCohort(
            string[] args, CohortExportOptions options, bool convert)
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
        //  Option parsing
        // -----------------------------------------------------------------------------------

        private static CohortExportOptions BuildOptions(string[] args, bool forConvert)
        {
            var options = new CohortExportOptions
            {
                InputRoot = CliArgs.RequireArg(args, "--input"),
                OutputRoot = CliArgs.RequireArg(args, "--output"),
                OnlyAssociatedRois = CliArgs.HasFlag(args, "--only-associated-rois"),
                Anonymize = CliArgs.HasFlag(args, "--anonymize"),
                PreferLargestSeries = CliArgs.HasFlag(args, "--prefer-largest-series"),
                RequireStructures = CliArgs.HasFlag(args, "--require-structures"),
                RequireDose = CliArgs.HasFlag(args, "--require-dose"),
                SeriesDescriptionFilter = CliArgs.OptionalArg(args, "--series-description"),
                StructDescriptionFilter = CliArgs.OptionalArg(args, "--struct-description"),
                FailFast = CliArgs.HasFlag(args, "--fail-fast"),
            };

            string salt = CliArgs.OptionalArg(args, "--salt");
            if (!string.IsNullOrEmpty(salt))
                options.Salt = salt;

            string keyPath = CliArgs.OptionalArg(args, "--key-path");
            if (!string.IsNullOrEmpty(keyPath))
            {
                if (!options.Anonymize)
                    throw new CliUsageException("--key-path places the anonymization key, so it needs --anonymize.");
                options.KeyPath = keyPath;
            }

            string manifestName = CliArgs.OptionalArg(args, "--manifest-name");
            if (!string.IsNullOrEmpty(manifestName))
                options.ManifestFileName = manifestName;

            string patients = CliArgs.OptionalArg(args, "--patients");
            if (!string.IsNullOrEmpty(patients))
                options.PatientIds = CohortExportOptions.ParseKeywordList(patients);

            // --target-spacing is accepted as an alias here for symmetry with --image-forward.
            string spacingArg = CliArgs.OptionalArgAny(args, "--output-spacing", "--target-spacing");
            if (!string.IsNullOrEmpty(spacingArg))
            {
                if (!CohortExportOptions.TryParseSpacing(spacingArg, out var spacing, out string error))
                    throw new CliUsageException($"--output-spacing: {error}");
                options.OutputSpacing = spacing;
            }

            string associationsPath = CliArgs.OptionalArg(args, "--associations");
            if (!string.IsNullOrEmpty(associationsPath))
            {
                if (!File.Exists(associationsPath))
                    throw new CliUsageException($"Associations file not found: {associationsPath}");
                options.Associations = new SettingsService().ImportAssociations(associationsPath);
                Console.Error.WriteLine($"  Loaded {options.Associations.Count} ROI association(s)");
            }

            if (forConvert)
            {
                options.ExportImages = !CliArgs.HasFlag(args, "--no-images");
                options.ExportStructures = !CliArgs.HasFlag(args, "--no-structures");
                options.ExportDoses = !CliArgs.HasFlag(args, "--no-doses");

                options.ImageKeywords = ValidateKeywords(
                    CliArgs.OptionalArg(args, "--metadata-tags"), "--metadata-tags",
                    MetadataTagCatalog.ImageComputedOptions);
                options.StructureKeywords = ValidateKeywords(
                    CliArgs.OptionalArg(args, "--metadata-structure-tags"), "--metadata-structure-tags",
                    MetadataTagCatalog.StructureComputedOptions);
                options.DoseKeywords = ValidateKeywords(
                    CliArgs.OptionalArg(args, "--metadata-dose-tags"), "--metadata-dose-tags",
                    MetadataTagCatalog.DoseComputedOptions);
            }
            else
            {
                options.ComputeVolumes = !CliArgs.HasFlag(args, "--no-volumes");
            }

            return options;
        }

        /// <summary>
        /// Splits a keyword list and rejects anything this metadata section cannot express.
        /// Failing here beats writing a metadata.json full of nulls that reads as "the tag was
        /// absent from the DICOM" rather than "it was misspelled on the command line".
        ///
        /// Computed pseudo-keywords are validated against the section's own options, so asking
        /// for "@MaxDose" under --metadata-tags is reported rather than silently ignored: the
        /// image section has no dose to measure.
        /// </summary>
        private static List<string> ValidateKeywords(
            string text, string flagName, IReadOnlyList<ComputedMetadataOption> computedOptions)
        {
            var keywords = CohortExportOptions.ParseKeywordList(text);
            if (keywords.Count == 0)
                return keywords;

            var knownTags = new HashSet<string>(
                DicomMetadataExtractor.GetSelectableTags().Select(t => t.Keyword),
                StringComparer.OrdinalIgnoreCase);
            var knownComputed = new HashSet<string>(
                computedOptions.Select(o => o.Key), StringComparer.OrdinalIgnoreCase);

            var unknown = keywords
                .Where(k => MetadataTagCatalog.IsComputedKey(k)
                    ? !knownComputed.Contains(k)
                    : !knownTags.Contains(k))
                .ToList();

            if (unknown.Count > 0)
            {
                throw new CliUsageException(
                    $"{flagName}: unrecognised keyword(s) {string.Join(", ", unknown)}. "
                    + "Expected fo-dicom tag keywords (PatientAge, KVP, DoseUnits) or one of this "
                    + $"section's computed values ({string.Join(", ", knownComputed)}).");
            }
            return keywords;
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
        private static void Emit(string[] args, object document)
        {
            string json = CohortDocuments.Serialize(document);
            Console.Out.WriteLine(json);

            string jsonOut = CliArgs.OptionalArg(args, "--json-out");
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
