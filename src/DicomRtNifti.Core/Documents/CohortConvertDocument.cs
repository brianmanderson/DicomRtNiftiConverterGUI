using System;
using System.Linq;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// The result of a cohort conversion (<c>--cohort-convert</c>): every file written per series,
    /// relative to the output root, plus what was skipped and what failed.
    /// </summary>
    public static class CohortConvertDocument
    {
        public const string Schema = "dicomrtnifti.cohort-convert/1";

        /// <param name="result">What <c>CohortExportService.ExecuteAsync</c> returned.</param>
        /// <param name="presenter">How the output root and key paths are shown.</param>
        public static object Build(CohortExportResult result, PathPresenter presenter)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            return new
            {
                schema = Schema,
                output_root = CohortDocuments.Present(presenter, result.OutputRoot),
                manifest_csv = result.ManifestCsv,
                anonymized = result.Anonymized,
                anonymization_key = CohortDocuments.Present(presenter, result.AnonymizationKey),
                output_spacing = result.OutputSpacing,
                succeeded = result.SucceededCount,
                failed = result.FailedCount,
                skipped = result.Skipped.Select(CohortDocuments.DescribeSkipped).ToList(),
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
                errors = result.Errors.Select(CohortDocuments.DescribeError).ToList(),
            };
        }
    }
}
