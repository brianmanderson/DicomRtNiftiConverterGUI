using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// The predicates behind the desktop app's badges, moved into Core so the web, the CLI and
    /// (from its next brief) the GUI raise them from one place. Each is a verbatim copy of the
    /// view-model rule it replaces, cited beside it; a test pins each one to its flag code.
    /// The rest are the analysis' own rules, which no badge shows yet.
    /// </summary>
    public static class SeriesFlags
    {
        /// <summary>
        /// The series the tree lists: CT, MR or PT, compared exactly as the scanner's linking does
        /// (<c>PatientGroupViewModel.IsImageSeries</c>).
        /// </summary>
        public static bool IsImageSeries(DicomSeriesGroup series)
            => series != null && (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT");

        /// <summary>The modalities the scanner links to image series (<c>StudyContents.IsLinkable</c>).</summary>
        public static bool IsLinkable(DicomSeriesGroup rtObject)
            => rtObject != null && (rtObject.Modality == "RTSTRUCT" || rtObject.Modality == "RTDOSE");

        /// <summary>
        /// The "non-uniform Z" badge: exactly the test the conversions use to decide whether to
        /// warn (<c>SeriesGroupViewModel</c>, <c>SeriesInspectorViewModel</c>), with their warning.
        /// </summary>
        public static bool IsNonUniformZ(DicomSeriesGroup series, out string warning)
            => SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(series, out warning);

        /// <summary>
        /// The series inspector's missing-position badge (<c>SeriesInspectorViewModel.HasMissingPositions</c>):
        /// slices skipped for lacking a usable ImagePositionPatient, with the conversions' warning.
        /// </summary>
        public static bool HasMissingPositions(DicomSeriesGroup series, out string warning)
            => SeriesGeometryProbe.TryBuildMissingPositionWarning(series, out warning);

        /// <summary>
        /// The "link guessed" badge on a series row: an RTSTRUCT or RTDOSE linked to it was
        /// attached by the largest-series fallback (<c>SeriesGroupViewModel.AnyLinkGuessed</c>).
        /// </summary>
        public static bool AnyLinkGuessed(DicomSeriesGroup series)
            => new[] { series.LinkedRtStruct, series.LinkedRtDose }
                .Concat(series.LinkedRtStructs ?? Enumerable.Empty<DicomSeriesGroup>())
                .Concat(series.LinkedRtDoses ?? Enumerable.Empty<DicomSeriesGroup>())
                .Any(rt => rt != null && rt.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);

        /// <summary>The study inspector's per-object "link guessed" (<c>StudyObjectRow.IsGuess</c>).</summary>
        public static bool IsLinkGuess(DicomSeriesGroup rtObject)
            => rtObject != null && rtObject.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback;

        /// <summary>
        /// The "unlinked" badge (<c>StudyContents.IsUnlinked</c>): an RTSTRUCT or RTDOSE that no image
        /// series of its study lists among its linked objects, so nothing exports it.
        /// </summary>
        public static bool IsUnlinked(DicomStudyGroup study, DicomSeriesGroup rtObject)
            => IsLinkable(rtObject) && !LinkedImageSeries(study, rtObject).Any();

        /// <summary>The image series of <paramref name="study"/> that list <paramref name="rtObject"/> among their linked RT objects.</summary>
        public static IEnumerable<DicomSeriesGroup> LinkedImageSeries(DicomStudyGroup study, DicomSeriesGroup rtObject)
            => (study?.Series ?? new List<DicomSeriesGroup>())
                .Where(IsImageSeries)
                .Where(image => LinksTo(image, rtObject));

        /// <summary>
        /// The study inspector's frames note (<c>StudyInspectorViewModel.HasMultipleFrames</c>): its
        /// image series span more than one non-empty frame of reference.
        /// </summary>
        public static bool HasMultipleFrames(DicomStudyGroup study, out int frameCount)
        {
            frameCount = (study?.Series ?? new List<DicomSeriesGroup>())
                .Where(IsImageSeries)
                .Select(s => s.FrameOfReferenceUID)
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.Ordinal)
                .Count();
            return frameCount > 1;
        }

        /// <summary>
        /// The ROI association browser's unmatched-name warning (<c>RoiAssociationViewModel.IsNameCovered</c>,
        /// negated): no association's canonical name or alias matches <paramref name="roiName"/>.
        /// </summary>
        public static bool IsUnassociated(string roiName, IEnumerable<RoiAssociation> associations)
        {
            foreach (var assoc in associations ?? Enumerable.Empty<RoiAssociation>())
            {
                if (assoc == null) continue;
                if (RoiNameMatcher.Matches(roiName, assoc.CanonicalName))
                    return false;
                foreach (var alias in assoc.Aliases ?? new List<string>())
                    if (RoiNameMatcher.Matches(roiName, alias))
                        return false;
            }
            return true;
        }

        // ---- The analysis' own rules ---------------------------------------------------------

        /// <summary>The slice normal is off the patient z axis (<see cref="SeriesOrientation.IsTilted"/>).</summary>
        public static bool IsOblique(DicomSeriesGroup series, out SeriesOrientation orientation)
        {
            orientation = series == null || series.OrientationVaries
                ? null
                : SeriesOrientation.FromImageOrientation(series.ImageOrientationPatient);
            return orientation != null && orientation.IsTilted;
        }

        /// <summary>The series' files do not share one ImageOrientationPatient.</summary>
        public static bool HasMixedOrientation(DicomSeriesGroup series)
            => series != null && series.OrientationVaries;

        /// <summary>Some instances carry a Modality the series' majority outvoted.</summary>
        public static bool HasModalityMinority(DicomSeriesGroup series)
            => series?.ModalityCounts != null && series.ModalityCounts.Count > 1;

        /// <summary>More than one structure set is linked to the series.</summary>
        public static bool HasMultipleStructureSets(DicomSeriesGroup series)
            => series?.LinkedRtStructs != null && series.LinkedRtStructs.Count > 1;

        /// <summary>
        /// An RT object linked to <paramref name="image"/> by its referenced SeriesInstanceUID names
        /// frames of reference, and the image's frame is none of them: the series test the link
        /// trusted and the frame test disagree. The frames are every one a structure set names
        /// (<see cref="DicomSeriesGroup.FrameOfReferenceUIDs"/>), so a structure set drawn across a
        /// planning CT and a registered MR is not flagged against the frame it also names.
        /// </summary>
        public static bool HasFrameOfReferenceMismatch(DicomSeriesGroup image, DicomSeriesGroup rtObject)
        {
            if (image == null || rtObject == null || rtObject.LinkMatchRule != RtLinkMatchRule.ReferencedSeriesUid)
                return false;
            if (string.IsNullOrEmpty(image.FrameOfReferenceUID))
                return false;
            var frames = FramesOf(rtObject);
            return frames.Count > 0 && !frames.Contains(image.FrameOfReferenceUID, StringComparer.Ordinal);
        }

        /// <summary>
        /// A structure set names an image series (its referenced SeriesInstanceUID, the first the
        /// scanner read) that no image series of its study carries: the series was not exported,
        /// or sits in another study. The scanner then fell back to the frame of reference or the
        /// largest series, or linked nothing. RTDOSE is not judged: its referenced UID is a
        /// structure set's SOP instance, not a series.
        /// </summary>
        public static bool ReferencesAbsentSeries(DicomStudyGroup study, DicomSeriesGroup rtObject)
        {
            if (rtObject == null || rtObject.Modality != "RTSTRUCT" || string.IsNullOrEmpty(rtObject.ReferencedSeriesUID))
                return false;
            return !(study?.Series ?? new List<DicomSeriesGroup>())
                .Where(IsImageSeries)
                .Any(image => image.SeriesInstanceUID == rtObject.ReferencedSeriesUID);
        }

        /// <summary>The frames of reference an RT object names: a structure set's full list, or its one frame.</summary>
        internal static List<string> FramesOf(DicomSeriesGroup rtObject)
        {
            var frames = (rtObject.FrameOfReferenceUIDs ?? new List<string>())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToList();
            if (frames.Count == 0 && !string.IsNullOrEmpty(rtObject.FrameOfReferenceUID))
                frames.Add(rtObject.FrameOfReferenceUID);
            return frames;
        }

        /// <summary><c>StudyContents.LinksTo</c>: by reference, through the singular and the list links.</summary>
        private static bool LinksTo(DicomSeriesGroup image, DicomSeriesGroup rtObject)
            => ReferenceEquals(image.LinkedRtStruct, rtObject)
               || ReferenceEquals(image.LinkedRtDose, rtObject)
               || (image.LinkedRtStructs?.Any(s => ReferenceEquals(s, rtObject)) ?? false)
               || (image.LinkedRtDoses?.Any(d => ReferenceEquals(d, rtObject)) ?? false);
    }
}
