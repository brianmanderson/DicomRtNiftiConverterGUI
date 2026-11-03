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
    ///
    /// Anonymized, it is held to the rule of the other documents: every PatientID, study and
    /// series UID (the RT objects' included) is the hash the export would use, and what has no
    /// hash is withheld — the input root, the unreadable files' paths, series dates, frames of
    /// reference, and the file names of unpositioned slices. Descriptions, ROI names, counts and
    /// geometry stay: they carry no identifier and they are what the inventory is for.
    /// </summary>
    public static class CohortScanDocument
    {
        public const string Schema = "dicomrtnifti.cohort-scan/1";

        /// <param name="scan">The scanned tree.</param>
        /// <param name="inputRoot">The folder that was scanned, as given; it is made full here.</param>
        /// <param name="presenter">How the input root is shown.</param>
        /// <param name="anon">
        /// The key to hash identifiers with, opened read-only (<see cref="ReadOnlyAnonymizationKey"/>);
        /// null for the plain inventory.
        /// </param>
        public static object Build(
            DicomScanResult scan, string inputRoot, PathPresenter presenter, AnonymizationService anon = null)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            var ids = new Identifiers(anon);

            return new
            {
                schema = Schema,
                input_root = ids.Anonymized
                    ? null
                    : CohortDocuments.Present(presenter, Path.GetFullPath(inputRoot)),
                scanned_files = scan.TotalFileCount,
                unreadable_files = scan.SkippedErrorCount,
                unreadable_samples = ids.Anonymized ? new List<string>() : scan.SkippedErrorSamples,
                roi_names = CollectRoiNames(scan),
                patients = scan.Patients.Select(p => DescribePatient(p, ids)).ToList(),
            };
        }

        /// <summary>Each identifier as the document shows it: as it is, or hashed.</summary>
        private sealed class Identifiers
        {
            private readonly AnonymizationService _anon;

            public Identifiers(AnonymizationService anon) { _anon = anon; }

            public bool Anonymized => _anon != null;

            public string Patient(string id) => _anon == null ? id : _anon.GetPatientHash(id ?? "");

            public string Study(string uid) => _anon == null ? uid : _anon.GetStudyHash(uid ?? "");

            public string Series(string uid) => _anon == null ? uid : _anon.GetSeriesHash(uid ?? "");

            /// <summary>What has no hash: shown as it is, or withheld.</summary>
            public string Withheld(string value) => _anon == null ? value : null;
        }

        private static object DescribePatient(DicomPatientGroup patient, Identifiers ids) => new
        {
            patient_id = ids.Patient(patient.PatientID),
            studies = patient.Studies.Select(study => new
            {
                study_instance_uid = ids.Study(study.StudyInstanceUID),
                series = study.Series
                    .Where(s => CohortDocuments.IsImageModality(s.Modality))
                    .Select(s => DescribeImageSeries(s, ids))
                    .ToList(),
                unlinked = study.Series
                    .Where(s => !CohortDocuments.IsImageModality(s.Modality) && s.LinkMatchRule == RtLinkMatchRule.None)
                    .Select(s => new
                    {
                        series_instance_uid = ids.Series(s.SeriesInstanceUID),
                        modality = s.Modality,
                        series_description = s.SeriesDescription,
                        reason = "did not match any image series in the study",
                    })
                    .ToList(),
            }).ToList(),
        };

        private static object DescribeImageSeries(DicomSeriesGroup series, Identifiers ids)
        {
            SeriesGeometryProbe.TryDescribe(series, out var geometry);

            return new
            {
                series_instance_uid = ids.Series(series.SeriesInstanceUID),
                modality = series.Modality,
                series_description = series.SeriesDescription,
                series_date = ids.Withheld(series.SeriesDate),
                frame_of_reference_uid = ids.Withheld(series.FrameOfReferenceUID),
                instance_count = series.FilePaths.Count,
                pixel_spacing = geometry?.PixelSpacing,
                slice_spacing = geometry?.SliceSpacing,
                slice_spacing_uniform = geometry?.Uniform,
                slice_spacing_values = geometry?.DistinctGaps,
                // Slices left out for lacking a usable ImagePositionPatient (F-12): the spacing
                // above judges the series without them, so a survey has to be able to see them.
                unpositioned_slices = ids.Anonymized
                    ? series.UnpositionedSlices.Select(CohortDocuments.WithoutFileName).ToList()
                    : series.UnpositionedSlices,
                linked_rtstructs = series.LinkedRtStructs.Select(rs => new
                {
                    series_instance_uid = ids.Series(rs.SeriesInstanceUID),
                    series_description = rs.SeriesDescription,
                    // How confident the link is. A study whose structure sets all resolved by
                    // LargestSeriesFallback needs a human to look at it.
                    match_rule = rs.LinkMatchRule.ToString(),
                    roi_names = rs.RoiNames,
                }).ToList(),
                linked_rtdoses = series.LinkedRtDoses.Select(rd => new
                {
                    series_instance_uid = ids.Series(rd.SeriesInstanceUID),
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
