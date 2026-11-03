using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// The inventory of a scanned tree (<c>--cohort-scan</c>): every patient, study and image
    /// series with its geometry and linked RT objects, and the RT objects that linked to nothing.
    /// </summary>
    public static class CohortScanDocument
    {
        public const string Schema = "dicomrtnifti.cohort-scan/1";

        /// <param name="scan">The scanned tree.</param>
        /// <param name="inputRoot">The folder that was scanned, as given; it is made full here.</param>
        /// <param name="presenter">How the input root is shown.</param>
        public static object Build(DicomScanResult scan, string inputRoot, PathPresenter presenter)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));

            return new
            {
                schema = Schema,
                input_root = CohortDocuments.Present(presenter, Path.GetFullPath(inputRoot)),
                scanned_files = scan.TotalFileCount,
                unreadable_files = scan.SkippedErrorCount,
                unreadable_samples = scan.SkippedErrorSamples,
                roi_names = CollectRoiNames(scan),
                patients = scan.Patients.Select(DescribePatient).ToList(),
            };
        }

        private static object DescribePatient(DicomPatientGroup patient) => new
        {
            patient_id = patient.PatientID,
            studies = patient.Studies.Select(study => new
            {
                study_instance_uid = study.StudyInstanceUID,
                series = study.Series
                    .Where(s => CohortDocuments.IsImageModality(s.Modality))
                    .Select(DescribeImageSeries)
                    .ToList(),
                unlinked = study.Series
                    .Where(s => !CohortDocuments.IsImageModality(s.Modality) && s.LinkMatchRule == RtLinkMatchRule.None)
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
    }
}
