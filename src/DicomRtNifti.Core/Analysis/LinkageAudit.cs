using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// How every RTSTRUCT and RTDOSE in a scan was attached to an image series, and what of that
    /// deserves a look before it becomes a training-set defect: links that are guesses
    /// (<see cref="RtLinkMatchRule.LargestSeriesFallback"/>), objects linked to nothing, links by
    /// referenced series whose frames of reference disagree, structure sets referencing a series
    /// their study does not hold, series carrying several structure sets or none, doses shared by
    /// several series, and studies whose series span several frames. It reads the links the
    /// scanner made and changes none of them.
    /// </summary>
    public static class LinkageAudit
    {
        private static readonly string[] Rules =
        {
            nameof(RtLinkMatchRule.ReferencedSeriesUid),
            nameof(RtLinkMatchRule.FrameOfReferenceUid),
            nameof(RtLinkMatchRule.LargestSeriesFallback),
            nameof(RtLinkMatchRule.None),
        };

        /// <summary>The linkage of <paramref name="scan"/>, with plain identifiers.</summary>
        public static LinkageSummary Audit(DicomScanResult scan) => Audit(scan, new AnalysisIdentifiers(null));

        internal static LinkageSummary Audit(DicomScanResult scan, AnalysisIdentifiers ids)
        {
            var summary = new LinkageSummary();
            foreach (var modality in new[] { "RTSTRUCT", "RTDOSE" })
                summary.RuleCounts[modality] = Rules.ToDictionary(r => r, r => 0);

            foreach (var patient in scan.Patients)
            {
                string patientId = ids.Patient(patient.PatientID);
                foreach (var study in patient.Studies)
                {
                    string studyUid = ids.Study(study.StudyInstanceUID);
                    var images = study.Series.Where(SeriesFlags.IsImageSeries).ToList();

                    if (images.Count > 0)
                    {
                        SeriesFlags.HasMultipleFrames(study, out int frames);
                        summary.FramesPerStudy.Add(new StudyFrames
                        {
                            PatientId = patientId,
                            StudyUid = studyUid,
                            ImageSeries = images.Count,
                            Frames = frames,
                            ImageSeriesWithoutFrame = images.Count(s => string.IsNullOrEmpty(s.FrameOfReferenceUID)),
                        });
                    }

                    foreach (var image in images)
                    {
                        if (image.LinkedRtStructs.Count == 0) summary.SeriesWithoutRtstruct++;
                        if (image.LinkedRtDoses.Count == 0) summary.SeriesWithoutRtdose++;

                        if (SeriesFlags.HasMultipleStructureSets(image))
                        {
                            summary.MultiStructSeries.Add(new MultiStructSeries
                            {
                                PatientId = patientId,
                                StudyUid = studyUid,
                                SeriesUid = ids.Series(image.SeriesInstanceUID),
                                StructureSets = image.LinkedRtStructs.Select(rs => ids.Series(rs.SeriesInstanceUID)).ToList(),
                                DefaultRtstruct = ids.Series(image.LinkedRtStruct?.SeriesInstanceUID),
                            });
                        }

                        foreach (var rt in image.LinkedRtStructs.Concat(image.LinkedRtDoses)
                                     .Where(rt => SeriesFlags.HasFrameOfReferenceMismatch(image, rt)))
                        {
                            var entry = Describe(patientId, studyUid, rt, ids);
                            entry.LinkedTo.Add(ids.Series(image.SeriesInstanceUID));
                            summary.ForMismatch.Add(entry);
                        }
                    }

                    foreach (var obj in study.Series.Where(s => !SeriesFlags.IsImageSeries(s)))
                    {
                        if (!SeriesFlags.IsLinkable(obj))
                        {
                            summary.OtherObjects.Add(Describe(patientId, studyUid, obj, ids));
                            continue;
                        }

                        summary.RuleCounts[obj.Modality][obj.LinkMatchRule.ToString()]++;
                        var linkedTo = SeriesFlags.LinkedImageSeries(study, obj)
                            .Select(img => ids.Series(img.SeriesInstanceUID))
                            .ToList();

                        if (SeriesFlags.IsUnlinked(study, obj))
                        {
                            var entry = Describe(patientId, studyUid, obj, ids);
                            entry.Reason = UnlinkedReason(images.Count);
                            summary.Unlinked.Add(entry);
                        }
                        if (SeriesFlags.ReferencesAbsentSeries(study, obj))
                        {
                            var entry = Describe(patientId, studyUid, obj, ids);
                            entry.LinkedTo.AddRange(linkedTo);
                            entry.ReferencedSeries = ids.Series(obj.ReferencedSeriesUID);
                            summary.ReferencedSeriesAbsent.Add(entry);
                        }
                        if (SeriesFlags.IsLinkGuess(obj))
                        {
                            var entry = Describe(patientId, studyUid, obj, ids);
                            entry.LinkedTo.AddRange(linkedTo);
                            summary.Fallback.Add(entry);
                        }
                        if (obj.Modality == "RTDOSE" && linkedTo.Count > 1)
                        {
                            var entry = Describe(patientId, studyUid, obj, ids);
                            entry.LinkedTo.AddRange(linkedTo);
                            summary.SharedDoses.Add(entry);
                        }
                    }
                }
            }
            return summary;
        }

        /// <summary>Why an RT object is unlinked, in <see cref="CohortExportService.BuildPlan"/>'s words.</summary>
        internal static string UnlinkedReason(int imageSeriesInStudy) =>
            imageSeriesInStudy == 0 ? "no image series in study" : "did not match any image series";

        private static LinkageObject Describe(string patientId, string studyUid, DicomSeriesGroup obj, AnalysisIdentifiers ids) =>
            new LinkageObject
            {
                PatientId = patientId,
                StudyUid = studyUid,
                ObjectUid = ids.Series(obj.SeriesInstanceUID),
                Modality = obj.Modality,
                SeriesDescription = obj.SeriesDescription,
                MatchRule = obj.LinkMatchRule.ToString(),
            };
    }

    /// <summary>
    /// Each identifier as an analysis shows it: as it is, or as the hash an anonymized export with
    /// the same key would write, so the analysis and the export can be joined.
    /// </summary>
    internal sealed class AnalysisIdentifiers
    {
        private readonly AnonymizationService _anon;

        public AnalysisIdentifiers(AnonymizationService anon) { _anon = anon; }

        public bool Anonymized => _anon != null;

        public string Patient(string id) => _anon == null ? id : _anon.GetPatientHash(id ?? "");

        public string Study(string uid) => _anon == null ? uid : _anon.GetStudyHash(uid ?? "");

        /// <summary>A SeriesInstanceUID, an RT object's included; null stays null.</summary>
        public string Series(string uid) => uid == null ? null : _anon == null ? uid : _anon.GetSeriesHash(uid);
    }
}
