using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// What a cohort run would do, before it does anything (<c>--dry-run</c> on
    /// <c>--cohort-manifest</c> and <c>--cohort-convert</c>): the
    /// <see cref="CohortExportService.BuildPlan"/> result. Every planned series with the folder it
    /// would be written to (after collision suffixing), the structure set chosen for it and the
    /// ROI names its masks would carry, every dose, the geometry warnings the run would log for
    /// it; then what was skipped and why, and the RT objects nothing would consume.
    ///
    /// Anonymized, it is held to the rule of the other documents: hashes for every identifier,
    /// the structure sets' and doses' included, no input path, and warnings that name the series
    /// by its hash and its unpositioned slices without their file names.
    /// </summary>
    public static class CohortPlanDocument
    {
        public const string Schema = "dicomrtnifti.cohort-plan/1";

        /// <param name="mode">The run this is a dry run of: manifest or convert.</param>
        /// <param name="plan">What <see cref="CohortExportService.BuildPlan"/> returned for these options.</param>
        /// <param name="anon">
        /// The key the plan was hashed with, opened read-only
        /// (<see cref="ReadOnlyAnonymizationKey"/>); null when not anonymizing.
        /// </param>
        /// <param name="keyFile">Where a real run would write the key; null when not anonymizing.</param>
        /// <param name="presenter">How the roots and the key path are shown.</param>
        public static object Build(
            CohortMode mode, CohortExportPlan plan, CohortExportOptions options,
            AnonymizationService anon, string keyFile, PathPresenter presenter)
        {
            if (mode == CohortMode.Scan)
                throw new ArgumentException("A scan writes nothing, so it has no plan.", nameof(mode));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));

            bool anonymized = options.Anonymize && anon != null;

            return new
            {
                schema = Schema,
                mode = CohortCommandLine.FlagFor(mode).TrimStart('-'),
                input_root = anonymized
                    ? null
                    : CohortDocuments.Present(presenter, Path.GetFullPath(options.InputRoot)),
                output_root = CohortDocuments.Present(presenter, Path.GetFullPath(options.OutputRoot)),
                manifest_csv = options.ManifestFileName,
                anonymized,
                anonymization_key = anonymized
                    ? CohortDocuments.Present(presenter, Path.GetFullPath(keyFile))
                    : null,
                output_spacing = options.OutputSpacing,
                series_count = plan.Series.Count,
                series = plan.Series.Select(p => DescribePlanned(p, options, anon, anonymized)).ToList(),
                skipped = plan.Skipped.Select(CohortDocuments.DescribeSkipped).ToList(),
                unlinked = plan.UnlinkedRtObjects.Select(CohortDocuments.DescribeSkipped).ToList(),
                warnings = DescribeRunWarnings(plan, options),
            };
        }

        private static object DescribePlanned(
            PlannedSeries planned, CohortExportOptions options, AnonymizationService anon, bool anonymized)
        {
            string Uid(DicomSeriesGroup rt) =>
                anonymized ? anon.GetSeriesHash(rt.SeriesInstanceUID ?? "") : rt.SeriesInstanceUID;

            var rtStruct = planned.RtStruct;
            return new
            {
                patient_id = planned.ExportPatientId,
                study_uid = planned.ExportStudyUid,
                series_uid = planned.ExportSeriesUid,
                modality = planned.Image.Modality,
                series_description = planned.Image.SeriesDescription,
                instance_count = planned.Image.FilePaths.Count,
                output_dir = planned.RelativeOutputDir,
                structure_set = rtStruct == null ? null : new
                {
                    series_uid = Uid(rtStruct),
                    series_description = rtStruct.SeriesDescription,
                    match_rule = rtStruct.LinkMatchRule.ToString(),
                    roi_names = rtStruct.RoiNames,
                },
                rois = ExportedRoiNames(planned, options),
                doses = planned.RtDoses.Select(dose => new
                {
                    series_uid = Uid(dose),
                    series_description = dose.SeriesDescription,
                    match_rule = dose.LinkMatchRule.ToString(),
                }).ToList(),
                warnings = DescribeSeriesWarnings(
                    CohortDocuments.ForAnonymizedText(planned.Image, planned.ExportSeriesUid, anonymized),
                    options),
            };
        }

        /// <summary>
        /// The names the masks (or the manifest's ROI columns) would carry: the structure set's ROI
        /// names through the associations (narrowed by any ROI selection), by the resolver the run
        /// itself uses, in the order the run records them. Empty when structures are not exported
        /// or there is no structure set.
        /// </summary>
        private static List<string> ExportedRoiNames(PlannedSeries planned, CohortExportOptions options)
        {
            if (!options.ExportStructures || planned.RtStruct == null)
                return new List<string>();
            var rois = CohortExportService.ResolveRois(options);
            return NiftiConversionService
                .ResolveRoiNames(planned.RtStruct.RoiNames, rois.Associations, rois.ExportUnmatched)
                .Keys
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The geometry warnings a run logs at the start of this series (CohortExportService's
        /// ReportSeriesStart): the skipped-slice warning, then the slice-spacing warning, which
        /// judges the reduced series.
        /// </summary>
        private static List<string> DescribeSeriesWarnings(DicomSeriesGroup image, CohortExportOptions options)
        {
            var warnings = new List<string>();
            if (SeriesGeometryProbe.TryBuildMissingPositionWarning(image, out string missing))
                warnings.Add(missing);
            if (SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(image, options.OutputSpacing, out string spacing))
                warnings.Add(spacing);
            return warnings;
        }

        /// <summary>
        /// What a run would warn about for the whole cohort, in the run's own words:
        /// --only-associated-rois with nothing to match against, or with associations (or an ROI
        /// selection) that match no ROI in any planned structure set (the run would export no mask
        /// and exit 1).
        /// </summary>
        private static List<string> DescribeRunWarnings(CohortExportPlan plan, CohortExportOptions options)
        {
            var warnings = new List<string>();
            var rois = CohortExportService.ResolveRois(options);
            if (rois.ExportUnmatched || !options.ExportStructures)
                return warnings;

            if (rois.Associations == null || rois.Associations.Count == 0)
            {
                warnings.Add(CohortExportService.OnlyAssociatedWithoutAssociationsWarning);
                return warnings;
            }

            var structureSets = plan.Series.Where(p => p.RtStruct != null).ToList();
            if (structureSets.Count > 0 && structureSets.All(p => ExportedRoiNames(p, options).Count == 0))
            {
                warnings.Add("WARNING: " +
                    CohortExportService.BuildNothingAssociatedMessage(options, structureSets));
            }
            return warnings;
        }
    }
}
