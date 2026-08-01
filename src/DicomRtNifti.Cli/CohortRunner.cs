using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

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
    ///   * Every document has a "schema" field so consumers can version-check.
    ///   * Per-series paths are relative to the output root and use forward slashes; only the
    ///     root itself is absolute.
    ///   * Under --anonymize the document contains hashes only — never a source PatientID,
    ///     StudyInstanceUID, or file path. Re-identification lives solely in AnonymizationKey.json.
    ///     Notebooks get committed, so their cell output must not carry PHI.
    /// Exit codes match the other modes: 0 ok, 1 conversion failure, 2 bad arguments.
    /// </summary>
    internal static class CohortRunner
    {
        public const string ScanSchema = "dicomrtnifti.cohort-scan/1";
        public const string ManifestSchema = "dicomrtnifti.cohort-manifest/1";
        public const string ConvertSchema = "dicomrtnifti.cohort-convert/1";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy(),
            },
            NullValueHandling = NullValueHandling.Include,
        };

        // -----------------------------------------------------------------------------------
        //  --cohort-scan
        // -----------------------------------------------------------------------------------

        public static int RunScan(string[] args)
        {
            string input = CliArgs.RequireArg(args, "--input");
            if (!Directory.Exists(input))
                throw new DirectoryNotFoundException($"Input folder not found: {input}");

            var scan = ScanTree(input);

            var document = new
            {
                schema = ScanSchema,
                input_root = Path.GetFullPath(input),
                scanned_files = scan.TotalFileCount,
                unreadable_files = scan.SkippedErrorCount,
                unreadable_samples = scan.SkippedErrorSamples,
                roi_names = CollectRoiNames(scan),
                patients = scan.Patients.Select(DescribePatient).ToList(),
            };

            Emit(args, document);
            return 0;
        }

        private static object DescribePatient(DicomPatientGroup patient) => new
        {
            patient_id = patient.PatientID,
            studies = patient.Studies.Select(study => new
            {
                study_instance_uid = study.StudyInstanceUID,
                series = study.Series
                    .Where(s => IsImageModality(s.Modality))
                    .Select(DescribeImageSeries)
                    .ToList(),
                unlinked = study.Series
                    .Where(s => !IsImageModality(s.Modality) && s.LinkMatchRule == RtLinkMatchRule.None)
                    .Select(s => new
                    {
                        series_instance_uid = s.SeriesInstanceUID,
                        modality = s.Modality,
                        series_description = s.SeriesDescription,
                        reason = "did not match any image series in the study",
                    })
                    .ToList(),
            }).ToList(),
        };

        private static object DescribeImageSeries(DicomSeriesGroup series)
        {
            SeriesGeometryProbe.TryDescribe(series, out var geometry);

            return new
            {
                series_instance_uid = series.SeriesInstanceUID,
                modality = series.Modality,
                series_description = series.SeriesDescription,
                series_date = series.SeriesDate,
                frame_of_reference_uid = series.FrameOfReferenceUID,
                instance_count = series.FilePaths.Count,
                pixel_spacing = geometry?.PixelSpacing,
                slice_spacing = geometry?.SliceSpacing,
                slice_spacing_uniform = geometry?.Uniform,
                slice_spacing_values = geometry?.DistinctGaps,
                linked_rtstructs = series.LinkedRtStructs.Select(rs => new
                {
                    series_instance_uid = rs.SeriesInstanceUID,
                    series_description = rs.SeriesDescription,
                    // How confident the link is. A study whose structure sets all resolved by
                    // LargestSeriesFallback needs a human to look at it.
                    match_rule = rs.LinkMatchRule.ToString(),
                    roi_names = rs.RoiNames,
                }).ToList(),
                linked_rtdoses = series.LinkedRtDoses.Select(rd => new
                {
                    series_instance_uid = rd.SeriesInstanceUID,
                    series_description = rd.SeriesDescription,
                    match_rule = rd.LinkMatchRule.ToString(),
                }).ToList(),
            };
        }

        // -----------------------------------------------------------------------------------
        //  --cohort-manifest / --cohort-convert
        // -----------------------------------------------------------------------------------

        public static int RunManifest(string[] args)
        {
            var options = BuildOptions(args, forConvert: false);
            var (result, _) = RunCohort(args, options, convert: false);

            Emit(args, new
            {
                schema = ManifestSchema,
                manifest_csv = Path.Combine(result.OutputRoot, result.ManifestCsv),
                anonymized = result.Anonymized,
                anonymization_key = result.AnonymizationKey,
                output_spacing = result.OutputSpacing,
                volumes_computed = result.VolumesComputed,
                roi_columns = result.RoiColumns,
                series_count = result.Series.Count,
                skipped = result.Skipped.Select(DescribeSkipped).ToList(),
                rows = result.Series.Select(s => new
                {
                    patient_id = s.PatientId,
                    study_uid = s.StudyUid,
                    series_uid = s.SeriesUid,
                    spacing = s.Spacing,
                    roi_volumes_cc = s.Masks.ToDictionary(m => m.Name, m => m.VolumeCc),
                }).ToList(),
                errors = result.Errors.Select(DescribeError).ToList(),
            });

            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        public static int RunConvert(string[] args)
        {
            var options = BuildOptions(args, forConvert: true);
            var (result, _) = RunCohort(args, options, convert: true);

            Emit(args, new
            {
                schema = ConvertSchema,
                output_root = result.OutputRoot,
                manifest_csv = result.ManifestCsv,
                anonymized = result.Anonymized,
                anonymization_key = result.AnonymizationKey,
                output_spacing = result.OutputSpacing,
                succeeded = result.SucceededCount,
                failed = result.FailedCount,
                skipped = result.Skipped.Select(DescribeSkipped).ToList(),
                series = result.Series.Select(s => new
                {
                    patient_id = s.PatientId,
                    study_uid = s.StudyUid,
                    series_uid = s.SeriesUid,
                    output_dir = s.OutputDir,
                    image = s.Image,
                    spacing = s.Spacing,
                    struct_link_rule = s.StructLinkRule,
                    masks = s.Masks.Select(m => new
                    {
                        name = m.Name,
                        volume_cc = m.VolumeCc,
                        file = m.File,
                    }).ToList(),
                    doses = s.Doses,
                    metadata = s.Metadata,
                }).ToList(),
                errors = result.Errors.Select(DescribeError).ToList(),
            });

            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }

        private static (CohortExportResult, CohortExportPlan) RunCohort(
            string[] args, CohortExportOptions options, bool convert)
        {
            if (!Directory.Exists(options.InputRoot))
                throw new DirectoryNotFoundException($"Input folder not found: {options.InputRoot}");

            // Load the key before the scan, not after. It is the one input that can refuse the run
            // outright — an unreadable key, or one recorded under a different salt — and refusing
            // after a full tree walk means the operator waits out a scan of the whole cohort to be
            // told the run was never going to start.
            AnonymizationService anon = null;
            if (options.Anonymize)
            {
                Directory.CreateDirectory(options.OutputRoot);
                anon = new AnonymizationService(
                    Path.Combine(options.OutputRoot, CohortExportService.AnonymizationKeyFileName),
                    options.Salt);
            }

            var scan = ScanTree(options.InputRoot);

            var plan = CohortExportService.BuildPlan(scan, options, anon);
            Console.Error.WriteLine(
                $"  Planned {plan.Series.Count} series ({plan.Skipped.Count} skipped, "
                + $"{plan.UnlinkedRtObjects.Count} unlinked RT objects)");

            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));
            var progress = new Progress<string>(msg => Console.Error.WriteLine("  " + msg));

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
                    throw new ArgumentException($"--output-spacing: {error}");
                options.OutputSpacing = spacing;
            }

            string associationsPath = CliArgs.OptionalArg(args, "--associations");
            if (!string.IsNullOrEmpty(associationsPath))
            {
                if (!File.Exists(associationsPath))
                    throw new FileNotFoundException($"Associations file not found: {associationsPath}");
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
                throw new ArgumentException(
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
            var progress = new Progress<string>(msg => Console.Error.WriteLine("  " + msg));
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

        private static List<string> CollectRoiNames(DicomScanResult scan)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in scan.Patients
                         .SelectMany(p => p.Studies)
                         .SelectMany(s => s.Series))
            {
                foreach (var roi in series.RoiNames)
                    if (seen.Add(roi))
                        names.Add(roi);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static object DescribeSkipped(SkippedSeries s) => new
        {
            patient_id = s.PatientId,
            series_instance_uid = s.SeriesInstanceUid,
            modality = s.Modality,
            series_description = s.SeriesDescription,
            reason = s.Reason,
        };

        private static object DescribeError(CohortError e) => new
        {
            patient_id = e.PatientId,
            series_uid = e.SeriesUid,
            message = e.Message,
        };

        private static bool IsImageModality(string modality) =>
            string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "MR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Writes the document to stdout, and to --json-out as well when given. Anonymous types
        /// already use snake_case member names, so the naming strategy only normalizes the
        /// PascalCase members reached through Core model objects.
        /// </summary>
        private static void Emit(string[] args, object document)
        {
            string json = JsonConvert.SerializeObject(document, JsonSettings);
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
