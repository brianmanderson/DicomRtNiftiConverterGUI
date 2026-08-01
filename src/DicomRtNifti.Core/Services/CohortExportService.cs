using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Drives a whole-cohort conversion: scan tree in, per-series NIfTI export plus a growable
    /// manifest out. This is the scriptable counterpart to the desktop app's Convert Selected
    /// and Export Manifest Only commands, and it exists so both front-ends can share one
    /// orchestration rather than one living inside a view-model.
    ///
    /// Planning is separated from execution on purpose: <see cref="BuildPlan"/> is pure and
    /// resolves every selection and naming decision, so those rules are testable without the
    /// SimpleITK native, and a caller can print the plan before committing to a long run.
    /// </summary>
    public class CohortExportService
    {
        private readonly NiftiConversionService _conversionService;

        public CohortExportService(NiftiConversionService conversionService)
        {
            _conversionService = conversionService
                ?? throw new ArgumentNullException(nameof(conversionService));
        }

        /// <summary>Anonymization key filename written into the cohort root.</summary>
        public const string AnonymizationKeyFileName = "AnonymizationKey.json";

        // -----------------------------------------------------------------------------------
        //  Planning (pure)
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Resolves which series will be exported, what they will be called, and where they will
        /// land. Performs no I/O beyond reading the already-scanned model.
        /// </summary>
        /// <param name="anon">
        /// Supplies the identifier hashes when <see cref="CohortExportOptions.Anonymize"/> is set.
        /// Ignored otherwise; may be null.
        /// </param>
        public static CohortExportPlan BuildPlan(
            DicomScanResult scan,
            CohortExportOptions options,
            AnonymizationService anon)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var plan = new CohortExportPlan();

            var patientFilter = (options.PatientIds != null && options.PatientIds.Count > 0)
                ? new HashSet<string>(options.PatientIds, StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var patient in scan.Patients)
            {
                if (patientFilter != null && !patientFilter.Contains(patient.PatientID ?? ""))
                    continue;

                foreach (var study in patient.Studies)
                {
                    var imageSeries = study.Series
                        .Where(s => IsImageModality(s.Modality))
                        .ToList();

                    // RT objects that never found a home. Their absence from the export is not
                    // a filter decision, so report them separately from Skipped.
                    foreach (var rt in study.Series.Where(s => !IsImageModality(s.Modality)))
                    {
                        if (rt.LinkMatchRule == RtLinkMatchRule.None)
                        {
                            plan.UnlinkedRtObjects.Add(Describe(patient.PatientID, rt,
                                imageSeries.Count == 0
                                    ? "no image series in study"
                                    : "did not match any image series",
                                options, anon));
                        }
                    }

                    var candidates = ApplySeriesSelection(
                        imageSeries, options, patient.PatientID, plan.Skipped, anon);

                    foreach (var image in candidates)
                    {
                        if (options.RequireStructures && image.LinkedRtStructs.Count == 0)
                        {
                            plan.Skipped.Add(Describe(patient.PatientID, image, "no linked RTSTRUCT", options, anon));
                            continue;
                        }
                        if (options.RequireDose && image.LinkedRtDoses.Count == 0)
                        {
                            plan.Skipped.Add(Describe(patient.PatientID, image, "no linked RTDOSE", options, anon));
                            continue;
                        }

                        plan.Series.Add(BuildPlannedSeries(
                            patient.PatientID, study.StudyInstanceUID, image, options, anon));
                    }
                }
            }

            return plan;
        }

        /// <summary>
        /// Narrows a study's image series to those that should be exported. Runs the description
        /// filter first, then the largest-series rule, recording a reason for everything dropped.
        /// </summary>
        private static List<DicomSeriesGroup> ApplySeriesSelection(
            List<DicomSeriesGroup> imageSeries,
            CohortExportOptions options,
            string patientId,
            List<SkippedSeries> skipped,
            AnonymizationService anon)
        {
            var candidates = imageSeries;

            if (!string.IsNullOrWhiteSpace(options.SeriesDescriptionFilter))
            {
                string needle = options.SeriesDescriptionFilter.Trim();
                var matched = new List<DicomSeriesGroup>();
                foreach (var s in candidates)
                {
                    bool hit = (s.SeriesDescription ?? "")
                        .IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hit) matched.Add(s);
                    else skipped.Add(Describe(patientId, s,
                        $"SeriesDescription does not contain '{needle}'", options, anon));
                }
                candidates = matched;
            }

            if (!string.IsNullOrWhiteSpace(options.StructDescriptionFilter))
            {
                string needle = options.StructDescriptionFilter.Trim();
                var matched = new List<DicomSeriesGroup>();
                foreach (var s in candidates)
                {
                    bool hit = s.LinkedRtStructs.Any(rs => (rs.SeriesDescription ?? "")
                        .IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit) matched.Add(s);
                    else skipped.Add(Describe(patientId, s,
                        $"no linked RTSTRUCT whose description contains '{needle}'", options, anon));
                }
                candidates = matched;
            }

            if (options.PreferLargestSeries && candidates.Count > 1)
            {
                // Ties break on SeriesInstanceUID so repeated runs pick the same series.
                var winner = candidates
                    .OrderByDescending(s => s.FilePaths.Count)
                    .ThenBy(s => s.SeriesInstanceUID, StringComparer.Ordinal)
                    .First();

                foreach (var s in candidates.Where(s => !ReferenceEquals(s, winner)))
                {
                    skipped.Add(Describe(patientId, s,
                        $"not the largest series in the study ({s.FilePaths.Count} slices vs {winner.FilePaths.Count})",
                        options, anon));
                }
                candidates = new List<DicomSeriesGroup> { winner };
            }

            return candidates;
        }

        /// <summary>
        /// Chooses which of an image series' structure sets to export. When
        /// <see cref="CohortExportOptions.StructDescriptionFilter"/> is set, the matching one wins
        /// — otherwise a series carrying both a planning-CT and a CBCT structure set would be
        /// filtered in on the strength of one and then exported with the other. Falls back to the
        /// first linked set, which is the scan-order first after the hierarchy is sorted.
        /// </summary>
        private static DicomSeriesGroup SelectStructureSet(
            DicomSeriesGroup image, CohortExportOptions options)
        {
            if (image.LinkedRtStructs.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(options.StructDescriptionFilter))
            {
                string needle = options.StructDescriptionFilter.Trim();
                var match = image.LinkedRtStructs.FirstOrDefault(rs =>
                    (rs.SeriesDescription ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                    return match;
            }

            return image.LinkedRtStructs[0];
        }

        private static PlannedSeries BuildPlannedSeries(
            string patientId,
            string studyUid,
            DicomSeriesGroup image,
            CohortExportOptions options,
            AnonymizationService anon)
        {
            string seriesUid = image.SeriesInstanceUID ?? "";
            patientId = patientId ?? "";
            studyUid = studyUid ?? "";

            var planned = new PlannedSeries
            {
                Image = image,
                RtStruct = SelectStructureSet(image, options),
                RtDoses = new List<DicomSeriesGroup>(image.LinkedRtDoses),
                PatientId = patientId,
                StudyUid = studyUid,
                SeriesUid = seriesUid,
            };

            if (options.Anonymize && anon != null)
            {
                planned.ExportPatientId = anon.GetPatientHash(patientId);
                planned.ExportStudyUid = anon.GetStudyHash(studyUid);
                planned.ExportSeriesUid = anon.GetSeriesHash(seriesUid);

                // Hashes are already path-safe; sanitize anyway so every segment is provably
                // valid on Windows regardless of how the hash alphabet changes.
                planned.RelativeOutputDir = Join(
                    WindowsPathSanitizer.SanitizeName(planned.ExportPatientId),
                    WindowsPathSanitizer.SanitizeName(planned.ExportStudyUid),
                    WindowsPathSanitizer.SanitizeName(planned.ExportSeriesUid));
            }
            else
            {
                planned.ExportPatientId = patientId;
                planned.ExportStudyUid = studyUid;
                planned.ExportSeriesUid = seriesUid;

                string seriesLabel = string.IsNullOrEmpty(image.SeriesDescription)
                    ? Truncate(seriesUid, 8)
                    : image.SeriesDescription;
                string dateLabel = string.IsNullOrEmpty(image.SeriesDate) ? "" : image.SeriesDate + "_";

                planned.RelativeOutputDir = Join(
                    WindowsPathSanitizer.SanitizeName(patientId),
                    WindowsPathSanitizer.SanitizeName(dateLabel + seriesLabel));
            }

            return planned;
        }

        // -----------------------------------------------------------------------------------
        //  Execution
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Converts every planned series: image, per-ROI masks, dose volumes and the metadata
        /// sidecar, then writes (merging into) the cohort manifest.
        /// </summary>
        public async Task<CohortExportResult> ExecuteAsync(
            CohortExportPlan plan,
            CohortExportOptions options,
            AnonymizationService anon,
            IProgress<string> progress,
            CancellationToken ct)
        {
            return await RunAsync(plan, options, anon, progress, ct, writeFiles: true)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Surveys the cohort without writing image, mask or dose files: spacing per series plus,
        /// when <see cref="CohortExportOptions.ComputeVolumes"/> is set, per-ROI volumes. Only the
        /// manifest is written.
        ///
        /// This still rasterizes when volumes are requested — there is no analytic contour-area
        /// path — so it saves the disk writes, not the compute.
        /// </summary>
        public async Task<CohortExportResult> ComputeManifestAsync(
            CohortExportPlan plan,
            CohortExportOptions options,
            AnonymizationService anon,
            IProgress<string> progress,
            CancellationToken ct)
        {
            return await RunAsync(plan, options, anon, progress, ct, writeFiles: false)
                .ConfigureAwait(false);
        }

        private async Task<CohortExportResult> RunAsync(
            CohortExportPlan plan,
            CohortExportOptions options,
            AnonymizationService anon,
            IProgress<string> progress,
            CancellationToken ct,
            bool writeFiles)
        {
            string outputRoot = Path.GetFullPath(options.OutputRoot);
            Directory.CreateDirectory(outputRoot);

            var result = new CohortExportResult
            {
                OutputRoot = outputRoot,
                ManifestCsv = options.ManifestFileName,
                Anonymized = options.Anonymize,
                AnonymizationKey = options.Anonymize ? AnonymizationKeyFileName : null,
                OutputSpacing = options.OutputSpacing,
                VolumesComputed = options.ComputeVolumes,
                Skipped = plan.Skipped,
            };

            var manifestRows = new List<ManifestRow>();
            var roiColumns = new List<string>();
            var roiColumnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int index = 0;
            foreach (var planned in plan.Series)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                string label = $"{planned.ExportPatientId}/{Truncate(planned.ExportSeriesUid, 8)}";
                progress?.Report($"[{index}/{plan.Series.Count}] {label}");

                // The non-uniform-Z diagnostic belonged here from the start and was not: the
                // single-series CLI modes warned, where a human is watching one conversion, and
                // the cohort modes — 400 patients, output read months later — did not. A mixed-gap
                // series converts at exit 0 either way; this is the only thing that says so.
                // Non-fatal, and it changes nothing about the geometry written.
                string spacingWarning;
                if (SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(
                        planned.Image, options.OutputSpacing, out spacingWarning))
                {
                    progress?.Report("  " + spacingWarning);
                }

                try
                {
                    var seriesResult = await ConvertSeriesAsync(
                        planned, options, outputRoot, progress, ct, writeFiles).ConfigureAwait(false);

                    result.Series.Add(seriesResult);
                    result.SucceededCount++;

                    var row = new ManifestRow
                    {
                        PatientID = planned.ExportPatientId,
                        StudyUID = planned.ExportStudyUid,
                        SeriesUID = planned.ExportSeriesUid,
                    };
                    if (seriesResult.Spacing != null && seriesResult.Spacing.Length == 3)
                    {
                        row.SpacingX = seriesResult.Spacing[0];
                        row.SpacingY = seriesResult.Spacing[1];
                        row.SpacingZ = seriesResult.Spacing[2];
                    }
                    foreach (var mask in seriesResult.Masks)
                    {
                        row.RoiVolumes[mask.Name] = mask.VolumeCc;
                        if (roiColumnSet.Add(mask.Name))
                            roiColumns.Add(mask.Name);
                    }
                    manifestRows.Add(row);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.Errors.Add(new CohortError
                    {
                        PatientId = planned.ExportPatientId,
                        SeriesUid = planned.ExportSeriesUid,
                        Message = ex.Message,
                    });
                    progress?.Report($"  failed: {ex.Message}");

                    if (options.FailFast)
                        throw;
                }
            }

            if (options.Anonymize && anon != null)
                anon.Save();

            result.RoiColumns = roiColumns;
            ExportManifestService.Write(
                Path.Combine(outputRoot, options.ManifestFileName), manifestRows, roiColumns);

            return result;
        }

        private async Task<SeriesExportResult> ConvertSeriesAsync(
            PlannedSeries planned,
            CohortExportOptions options,
            string outputRoot,
            IProgress<string> progress,
            CancellationToken ct,
            bool writeFiles)
        {
            string outputDir = Path.Combine(outputRoot,
                planned.RelativeOutputDir.Replace('/', Path.DirectorySeparatorChar));

            var seriesResult = new SeriesExportResult
            {
                PatientId = planned.ExportPatientId,
                StudyUid = planned.ExportStudyUid,
                SeriesUid = planned.ExportSeriesUid,
                OutputDir = planned.RelativeOutputDir,
                StructLinkRule = planned.RtStruct?.LinkMatchRule.ToString(),
            };

            if (writeFiles)
                Directory.CreateDirectory(outputDir);

            double[] spacing = null;

            if (writeFiles && options.ExportImages)
            {
                spacing = await Task.Run(() => _conversionService.ConvertImageSeriesToNifti(
                    planned.Image, outputDir, progress, ct, options.OutputSpacing), ct)
                    .ConfigureAwait(false);
                seriesResult.Image = Join(planned.RelativeOutputDir, NiftiFileNaming.ImageNiiGz);
            }

            if (spacing == null)
            {
                spacing = await Task.Run(() => _conversionService.GetImageSpacing(planned.Image), ct)
                    .ConfigureAwait(false);
            }

            // Report the grid actually written. When resampling, that is the target, not the
            // source series' native spacing.
            if (options.OutputSpacing != null)
                spacing = (double[])options.OutputSpacing.Clone();

            seriesResult.Spacing = spacing;

            var effectiveAssociations = options.Associations;
            bool exportUnmatched = !options.OnlyAssociatedRois;

            if (options.ExportStructures && planned.RtStruct != null)
            {
                Dictionary<string, double> roiVolumes;

                if (writeFiles)
                {
                    roiVolumes = await Task.Run(() => _conversionService.ConvertStructToNifti(
                        planned.RtStruct, planned.Image, outputDir,
                        effectiveAssociations, exportUnmatched,
                        false, progress, ct, options.OutputSpacing), ct).ConfigureAwait(false);
                }
                else if (options.ComputeVolumes)
                {
                    roiVolumes = await Task.Run(() => _conversionService.ComputeStructVolumes(
                        planned.RtStruct, planned.Image,
                        effectiveAssociations, exportUnmatched,
                        progress, ct, options.OutputSpacing), ct).ConfigureAwait(false);
                }
                else
                {
                    // Name the ROIs without rasterizing, so the manifest still gains its columns.
                    roiVolumes = planned.RtStruct.RoiNames
                        .ToDictionary(n => n, _ => ExportManifestService.MissingValue,
                                      StringComparer.OrdinalIgnoreCase);
                }

                if (roiVolumes != null)
                {
                    // Same order-independent naming the writer used, so ROI names that sanitize to
                    // the same string are recorded at the distinct paths they were written to.
                    var maskFileNames = NiftiConversionService.BuildUniqueMaskFileNames(roiVolumes.Keys);
                    foreach (var kv in roiVolumes.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        seriesResult.Masks.Add(new MaskExportResult
                        {
                            Name = kv.Key,
                            VolumeCc = kv.Value,
                            File = writeFiles
                                ? Join(planned.RelativeOutputDir, "masks",
                                       maskFileNames[kv.Key] + ".nii.gz")
                                : null,
                        });
                    }
                }
            }

            if (writeFiles && options.ExportDoses && planned.RtDoses.Count > 0)
            {
                // Every linked dose, not just the first: a study can carry per-beam volumes and
                // a plan sum, and exporting one of them silently loses the rest. The reserved-name
                // set is scoped to this series' run so repeated descriptions get suffixed while a
                // re-export still overwrites its own previous output rather than accumulating.
                var doseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dose in planned.RtDoses)
                {
                    string written = await Task.Run(() => _conversionService.ConvertDoseToNifti(
                        dose, outputDir, progress, ct, options.OutputSpacing, doseNames), ct)
                        .ConfigureAwait(false);

                    // Take the path the writer chose rather than rebuilding it here — it owns
                    // the sanitizing and collision-suffix rules.
                    if (written != null)
                        seriesResult.Doses.Add(Relative(outputRoot, written));
                }
            }

            if (writeFiles && options.HasMetadataSelections)
            {
                var request = new MetadataExportRequest
                {
                    ImageFilePaths = planned.Image.FilePaths,
                    StructureFilePath = planned.RtStruct != null && planned.RtStruct.FilePaths.Count > 0
                        ? planned.RtStruct.FilePaths[0] : null,
                    DoseFilePath = planned.RtDoses.Count > 0 && planned.RtDoses[0].FilePaths.Count > 0
                        ? planned.RtDoses[0].FilePaths[0] : null,
                    ImageKeywords = options.ImageKeywords,
                    StructureKeywords = options.StructureKeywords,
                    DoseKeywords = options.DoseKeywords,
                    ImageVoxelSpacing = spacing,
                };

                string metaPath = Path.Combine(outputDir, "metadata.json");
                await Task.Run(() => DicomMetadataExtractor.WriteMetadataJson(request, metaPath), ct)
                    .ConfigureAwait(false);
                seriesResult.Metadata = Join(planned.RelativeOutputDir, "metadata.json");
            }

            return seriesResult;
        }

        // -----------------------------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------------------------

        private static bool IsImageModality(string modality) =>
            string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "MR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Describes an excluded series for reporting. Identifiers are hashed whenever the export
        /// is anonymized, because this record is serialized into the cohort JSON alongside the
        /// exported series — a skipped series must not be the hole through which a real
        /// PatientID or SeriesInstanceUID reaches a notebook's committed cell output.
        ///
        /// SeriesDescription and modality are kept either way: they carry no HIPAA identifier and
        /// they are the whole point of the record, since "skipped because the description did not
        /// match" is unactionable without them.
        /// </summary>
        private static SkippedSeries Describe(
            string patientId, DicomSeriesGroup series, string reason,
            CohortExportOptions options, AnonymizationService anon)
        {
            bool hash = options.Anonymize && anon != null;
            return new SkippedSeries
            {
                PatientId = hash ? anon.GetPatientHash(patientId ?? "") : (patientId ?? ""),
                SeriesInstanceUid = hash
                    ? anon.GetSeriesHash(series.SeriesInstanceUID ?? "")
                    : (series.SeriesInstanceUID ?? ""),
                Modality = series.Modality ?? "",
                SeriesDescription = series.SeriesDescription ?? "",
                Reason = reason,
            };
        }

        /// <summary>Joins path segments with forward slashes, the form used in the JSON contracts.</summary>
        private static string Join(params string[] segments) =>
            string.Join("/", segments.Where(s => !string.IsNullOrEmpty(s)));

        /// <summary>
        /// Expresses an absolute path relative to the cohort root, with forward slashes. Falls
        /// back to the filename if the path somehow sits outside the root.
        /// </summary>
        private static string Relative(string root, string fullPath)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalized = Path.GetFullPath(fullPath);

            if (normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(normalizedRoot.Length + 1).Replace('\\', '/');
            }
            return Path.GetFileName(normalized);
        }

        private static string Truncate(string value, int length) =>
            string.IsNullOrEmpty(value) ? "" : value.Substring(0, Math.Min(length, value.Length));
    }
}
