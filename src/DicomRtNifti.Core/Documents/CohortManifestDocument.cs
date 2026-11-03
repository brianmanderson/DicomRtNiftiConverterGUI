using System;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// The result of a cohort survey (<c>--cohort-manifest</c>): the manifest's rows — spacing and
    /// per-ROI volume per series — plus what was skipped and what failed.
    /// </summary>
    public static class CohortManifestDocument
    {
        public const string Schema = "dicomrtnifti.cohort-manifest/1";

        /// <param name="result">What <c>CohortExportService.ComputeManifestAsync</c> returned.</param>
        /// <param name="presenter">How the manifest and key paths are shown.</param>
        public static object Build(CohortExportResult result, PathPresenter presenter)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            return new
            {
                schema = Schema,
                manifest_csv = CohortDocuments.Present(presenter, Path.Combine(result.OutputRoot, result.ManifestCsv)),
                anonymized = result.Anonymized,
                anonymization_key = CohortDocuments.Present(presenter, result.AnonymizationKey),
                output_spacing = result.OutputSpacing,
                volumes_computed = result.VolumesComputed,
                roi_columns = result.RoiColumns,
                series_count = result.Series.Count,
                skipped = result.Skipped.Select(CohortDocuments.DescribeSkipped).ToList(),
                rows = result.Series.Select(s => new
                {
                    patient_id = s.PatientId,
                    study_uid = s.StudyUid,
                    series_uid = s.SeriesUid,
                    spacing = s.Spacing,
                    roi_volumes_cc = s.Masks.ToDictionary(m => m.Name, m => m.VolumeCc),
                }).ToList(),
                errors = result.Errors.Select(CohortDocuments.DescribeError).ToList(),
            };
        }
    }
}
