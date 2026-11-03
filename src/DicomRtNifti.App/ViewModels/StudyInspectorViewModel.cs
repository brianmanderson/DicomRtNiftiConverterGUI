using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// The study inspector: everything one scanned study holds, including what the tree leaves
    /// out. The tree lists only the image series (CT / MR / PT) and shows RT objects under the
    /// series they linked to, so an RTSTRUCT or RTDOSE linked to nothing, an RTPLAN, or any other
    /// non-image series is invisible there; this is where it shows. Everything above the tag grid
    /// is a pure mapping of the scanned <see cref="DicomStudyGroup"/>, with no file access. The tag
    /// grid reads the study-level curated tags from the first slice of the study's first image series.
    /// </summary>
    public class StudyInspectorViewModel : InspectorViewModel
    {
        /// <summary>
        /// The study-level keywords of <see cref="MetadataTagCatalog.CuratedImageKeywords"/>, in its
        /// order: its study group, the General Study attributes it curates.
        /// </summary>
        public static IReadOnlyList<string> StudyKeywords { get; } = new[]
        {
            "StudyDate", "StudyTime", "StudyDescription", "StudyID", "AccessionNumber",
            "ReferringPhysicianName", "StudyInstanceUID",
        };

        /// <summary>The amber note under an "unlinked" badge; the help explains the badge in the same words.</summary>
        public const string UnlinkedNote =
            "An RT object badged unlinked is linked to no image series in its study, so Convert Selected and " +
            "Export Manifest Only never export it: structures and doses are exported only with the image series " +
            "they are linked to. The scan leaves an RTSTRUCT or RTDOSE unlinked when its study holds no CT, MR " +
            "or PT series, which happens when its images are in another study or were not in the input folder.";

        private readonly StudyContents _contents;

        public StudyInspectorViewModel(DicomStudyGroup model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            var contents = _contents = new StudyContents(model);

            Title = string.IsNullOrWhiteSpace(model.StudyDescription)
                ? "(no study description)"
                : model.StudyDescription.Trim();
            StudyInstanceUid = string.IsNullOrEmpty(model.StudyInstanceUID) ? Missing : model.StudyInstanceUID;
            StudyDateText = FormatDicomDate(model.StudyDate);

            // Frames in order of first appearance, so "frame 1" is the first series' frame.
            var frames = contents.ImageSeries
                .Select(s => s.FrameOfReferenceUID)
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            FrameOfReferenceCount = frames.Count;
            int withoutFrame = contents.ImageSeries.Count(s => string.IsNullOrEmpty(s.FrameOfReferenceUID));
            HasMultipleFrames = frames.Count > 1;
            FramesText = contents.ImageSeries.Count == 0
                ? Missing
                : (frames.Count == 0 ? "none recorded" : frames.Count.ToString(CultureInfo.InvariantCulture))
                  + (withoutFrame > 0 && frames.Count > 0
                      ? string.Format(CultureInfo.InvariantCulture, " ({0} image series carry none)", withoutFrame)
                      : "");
            MultipleFramesNote = HasMultipleFrames
                ? string.Format(CultureInfo.InvariantCulture,
                    "The image series in this study span {0} frames of reference, so they do not share one patient " +
                    "coordinate system. A structure set or dose applies to the series of its own frame; its masks do " +
                    "not overlay a series in another frame without a registration.", frames.Count)
                : "";

            ImageSeries = contents.ImageSeries
                .Select(s => new StudySeriesRow(s, HasMultipleFrames ? FrameLabel(s, frames) : ""))
                .ToList();
            ImageSeriesSummary = StudyContents.DescribeModalityCounts(contents.ImageSeries);
            NonUniformSeriesCount = ImageSeries.Count(r => r.IsNonUniformZ);
            GuessedLinkSeriesCount = ImageSeries.Count(r => r.HasGuessedLink);

            RtObjects = contents.Objects.Select(o => BuildObjectRow(o, contents)).ToList();
            UnlinkedObjectCount = RtObjects.Count(o => o.IsUnlinked);
        }

        public DicomStudyGroup Model { get; }

        public override string Header => "Study inspector";

        public string Title { get; }
        public string StudyInstanceUid { get; }

        /// <summary>StudyDate as "2024-03-05", or <see cref="InspectorViewModel.Missing"/>.</summary>
        public string StudyDateText { get; }

        /// <summary>The image series the tree lists for this study, in tree order.</summary>
        public IReadOnlyList<StudySeriesRow> ImageSeries { get; }
        public int ImageSeriesCount => ImageSeries.Count;
        public bool HasImageSeries => ImageSeries.Count > 0;

        /// <summary>"2 CT, 1 MR", or "none".</summary>
        public string ImageSeriesSummary { get; }

        /// <summary>Every other series of the study: RTSTRUCT, RTDOSE, RTPLAN and any other non-image modality.</summary>
        public IReadOnlyList<StudyObjectRow> RtObjects { get; }
        public int RtObjectCount => RtObjects.Count;
        public bool HasRtObjects => RtObjects.Count > 0;

        /// <summary>Distinct non-empty frames of reference among the image series.</summary>
        public int FrameOfReferenceCount { get; }
        public bool HasMultipleFrames { get; }

        /// <summary>"1", "2 (1 image series carry none)", "none recorded", or <see cref="InspectorViewModel.Missing"/> with no image series.</summary>
        public string FramesText { get; }

        /// <summary>Why more than one frame matters; empty when <see cref="HasMultipleFrames"/> is false.</summary>
        public string MultipleFramesNote { get; }

        public int NonUniformSeriesCount { get; }
        public bool HasNonUniformSeries => NonUniformSeriesCount > 0;
        public string NonUniformBadgeText => NonUniformSeriesCount == 1
            ? "non-uniform Z"
            : $"{NonUniformSeriesCount} non-uniform Z";

        public int GuessedLinkSeriesCount { get; }
        public bool HasGuessedLinkSeries => GuessedLinkSeriesCount > 0;
        public string GuessedLinkBadgeText => GuessedLinkSeriesCount == 1
            ? "link guessed"
            : $"{GuessedLinkSeriesCount} links guessed";

        /// <summary>RTSTRUCT and RTDOSE objects of this study that no image series in it is linked to.</summary>
        public int UnlinkedObjectCount { get; }
        public bool HasUnlinkedObjects => UnlinkedObjectCount > 0;
        public string UnlinkedBadgeText => UnlinkedObjectCount == 1 ? "unlinked" : $"{UnlinkedObjectCount} unlinked";
        public string UnlinkedObjectsNote => UnlinkedNote;

        protected override string NoTagsText => "No curated study tags present in the first slice of this study's first image series.";

        protected override IReadOnlyList<InspectorTagSource> TagSources() => new[]
        {
            new InspectorTagSource("Study", _contents.FirstTagFile(), StudyKeywords),
        };

        private static string FrameLabel(DicomSeriesGroup series, List<string> frames)
        {
            int index = string.IsNullOrEmpty(series.FrameOfReferenceUID)
                ? -1
                : frames.IndexOf(series.FrameOfReferenceUID);
            return index < 0
                ? "no frame of reference"
                : string.Format(CultureInfo.InvariantCulture, "frame {0}", index + 1);
        }

        private static StudyObjectRow BuildObjectRow(DicomSeriesGroup rtObject, StudyContents contents)
        {
            string kind = string.IsNullOrEmpty(rtObject.Modality) ? Missing : rtObject.Modality;
            string detail = rtObject.Modality == "RTSTRUCT"
                ? string.Format(CultureInfo.InvariantCulture, "{0} ROI(s)", rtObject.RoiNames?.Count ?? 0)
                : string.Format(CultureInfo.InvariantCulture, "{0} file(s)", rtObject.FilePaths?.Count ?? 0);

            // The scanner links only structure sets and doses; a plan or any other series is listed
            // so the study is fully accounted for, not flagged for lacking a link nothing makes.
            if (!StudyContents.IsLinkable(rtObject))
                return new StudyObjectRow(kind, DescribeObject(rtObject), detail,
                    "listed only: the scan links only RTSTRUCT and RTDOSE, and nothing exports this", false, false);

            var targets = contents.LinkedImageSeries(rtObject);
            if (targets.Count == 0)
                return new StudyObjectRow(kind, DescribeObject(rtObject), detail,
                    "linked to no image series in this study", true, false);

            string link = string.Format(CultureInfo.InvariantCulture, "linked to {0}, by {1}",
                string.Join(", ", targets.Select(t => string.Format(CultureInfo.InvariantCulture, "[{0}] {1}",
                    string.IsNullOrEmpty(t.Modality) ? Missing : t.Modality, DescribeObject(t)))),
                LinkRuleLabel(rtObject.LinkMatchRule));
            return new StudyObjectRow(kind, DescribeObject(rtObject), detail, link, false,
                rtObject.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);
        }
    }

    /// <summary>
    /// A scanned study split the way the tree and the scanner split it: the image series the tree
    /// lists, and every other series. Pure; shared by the patient and study inspectors so their
    /// counts agree.
    /// </summary>
    internal sealed class StudyContents
    {
        public StudyContents(DicomStudyGroup study)
        {
            var series = study?.Series ?? new List<DicomSeriesGroup>();
            ImageSeries = series.Where(StudyGroupViewModel.IsImageSeries).ToList();
            Objects = series.Where(s => s != null && !StudyGroupViewModel.IsImageSeries(s)).ToList();
        }

        public List<DicomSeriesGroup> ImageSeries { get; }
        public List<DicomSeriesGroup> Objects { get; }

        /// <summary>The modalities the scanner links to image series.</summary>
        public static bool IsLinkable(DicomSeriesGroup rtObject)
            => rtObject.Modality == "RTSTRUCT" || rtObject.Modality == "RTDOSE";

        /// <summary>The image series of this study that list <paramref name="rtObject"/> among their linked RT objects.</summary>
        public List<DicomSeriesGroup> LinkedImageSeries(DicomSeriesGroup rtObject)
            => ImageSeries.Where(image => LinksTo(image, rtObject)).ToList();

        /// <summary>An RTSTRUCT or RTDOSE no image series of this study is linked to: nothing exports it.</summary>
        public bool IsUnlinked(DicomSeriesGroup rtObject)
            => IsLinkable(rtObject) && !ImageSeries.Any(image => LinksTo(image, rtObject));

        /// <summary>
        /// The file the patient and study tag grids read: the first slice of the first image series,
        /// or, in a study with none, the first file of its first other series (an RT object carries
        /// the patient and study modules too).
        /// </summary>
        public string FirstTagFile()
            => ImageSeries.Select(InspectorViewModel.FirstFile).FirstOrDefault(p => p != null)
               ?? Objects.Select(InspectorViewModel.FirstFile).FirstOrDefault(p => p != null);

        private static readonly string[] ImageModalityOrder = { "CT", "MR", "PT" };

        /// <summary>"3 CT, 1 MR" in CT, MR, PT order; "none" for no series.</summary>
        public static string DescribeModalityCounts(IEnumerable<DicomSeriesGroup> imageSeries)
        {
            var counts = imageSeries
                .GroupBy(s => s.Modality)
                .OrderBy(g => Array.IndexOf(ImageModalityOrder, g.Key))
                .Select(g => string.Format(CultureInfo.InvariantCulture, "{0} {1}", g.Count(), g.Key))
                .ToList();
            return counts.Count == 0 ? "none" : string.Join(", ", counts);
        }

        private static bool LinksTo(DicomSeriesGroup image, DicomSeriesGroup rtObject)
            => ReferenceEquals(image.LinkedRtStruct, rtObject)
               || ReferenceEquals(image.LinkedRtDose, rtObject)
               || (image.LinkedRtStructs?.Any(s => ReferenceEquals(s, rtObject)) ?? false)
               || (image.LinkedRtDoses?.Any(d => ReferenceEquals(d, rtObject)) ?? false);
    }

    /// <summary>One image series of the inspected study, with the badges its tree row carries.</summary>
    public class StudySeriesRow
    {
        public StudySeriesRow(DicomSeriesGroup series, string frameLabel)
        {
            Modality = string.IsNullOrEmpty(series.Modality) ? InspectorViewModel.Missing : series.Modality;
            Description = InspectorViewModel.DescribeObject(series);
            InstanceCount = series.FilePaths?.Count ?? 0;
            FrameLabel = frameLabel ?? "";

            // The tree row's own tests, so a badge here is the badge on that row.
            string warning;
            IsNonUniformZ = SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(series, out warning);
            NonUniformZWarning = warning ?? "";
            HasGuessedLink = SeriesGroupViewModel.AnyLinkGuessed(series);

            Detail = FrameLabel.Length == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} instance(s)", InstanceCount)
                : string.Format(CultureInfo.InvariantCulture, "{0} instance(s) · {1}", InstanceCount, FrameLabel);
        }

        public string Modality { get; }
        public string Description { get; }
        public int InstanceCount { get; }

        /// <summary>"frame 1", "frame 2" when the study's series span several frames of reference; empty otherwise.</summary>
        public string FrameLabel { get; }

        /// <summary>"120 instance(s)", with " · frame 2" appended when the frame label is set.</summary>
        public string Detail { get; }

        public bool IsNonUniformZ { get; }
        public string NonUniformZWarning { get; }
        public bool HasGuessedLink { get; }
    }

    /// <summary>One non-image series of the inspected study, and the image series it is linked to.</summary>
    public class StudyObjectRow
    {
        public StudyObjectRow(string kind, string description, string detail, string link, bool isUnlinked, bool isGuess)
        {
            Kind = kind;
            Description = description;
            Detail = detail;
            Link = link;
            IsUnlinked = isUnlinked;
            IsGuess = isGuess;
        }

        /// <summary>The modality: RTSTRUCT, RTDOSE, RTPLAN, or whatever else the study holds.</summary>
        public string Kind { get; }
        public string Description { get; }

        /// <summary>"3 ROI(s)" for a structure set, "1 file(s)" otherwise.</summary>
        public string Detail { get; }

        /// <summary>"linked to [CT] Planning CT, by referenced series UID", "linked to no image series in this study", or why it is only listed.</summary>
        public string Link { get; }

        /// <summary>An RTSTRUCT or RTDOSE no image series of the study is linked to.</summary>
        public bool IsUnlinked { get; }

        /// <summary>Linked by the largest-series fallback.</summary>
        public bool IsGuess { get; }
    }
}
