using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// The patient inspector: what a cohort folder holds for one patient, without expanding the
    /// tree. Everything above the tag grid is a pure mapping of the scanned
    /// <see cref="DicomPatientGroup"/>, split per study by the same rules the tree and the study
    /// inspector use (<see cref="StudyContents"/>), with no file access. The tag grid reads the
    /// patient-level curated tags from the first slice of the patient's first image series.
    ///
    /// The badge counts use the series rows' own tests (<see cref="SeriesGeometryProbe"/> and
    /// <see cref="SeriesGroupViewModel.AnyLinkGuessed"/>), so they are the patient row's counts.
    /// </summary>
    public class PatientInspectorViewModel : InspectorViewModel
    {
        /// <summary>The patient-level keywords of <see cref="MetadataTagCatalog.CuratedImageKeywords"/>, in its order.</summary>
        public static IReadOnlyList<string> PatientKeywords { get; } = new[]
        {
            "PatientName", "PatientID", "PatientBirthDate", "PatientSex", "PatientAge", "PatientWeight",
        };

        private readonly List<StudyContents> _contents;

        public PatientInspectorViewModel(DicomPatientGroup model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));

            PatientId = string.IsNullOrEmpty(model.PatientID) ? Missing : model.PatientID;
            PatientName = string.IsNullOrEmpty(model.PatientName) ? Missing : model.PatientName;

            var studies = (model.Studies ?? new List<DicomStudyGroup>())
                .Select(s => new KeyValuePair<DicomStudyGroup, StudyContents>(s, new StudyContents(s)))
                .ToList();
            _contents = studies.Select(s => s.Value).ToList();

            var images = _contents.SelectMany(c => c.ImageSeries).ToList();
            ImageSeriesCount = images.Count;
            ImageSeriesSummary = StudyContents.DescribeModalityCounts(images);
            ImageInstanceCount = images.Sum(s => s.FilePaths?.Count ?? 0);

            string ignored;
            NonUniformSeriesCount = images.Count(s => SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(s, out ignored));
            GuessedLinkSeriesCount = images.Count(SeriesGroupViewModel.AnyLinkGuessed);

            int linkedStructs = 0, unlinkedStructs = 0, linkedDoses = 0, unlinkedDoses = 0;
            foreach (var contents in _contents)
                foreach (var rtObject in contents.Objects)
                {
                    bool unlinked = contents.IsUnlinked(rtObject);
                    if (rtObject.Modality == "RTSTRUCT")
                    {
                        if (unlinked) unlinkedStructs++; else linkedStructs++;
                    }
                    else if (rtObject.Modality == "RTDOSE")
                    {
                        if (unlinked) unlinkedDoses++; else linkedDoses++;
                    }
                }
            LinkedRtStructCount = linkedStructs;
            LinkedRtDoseCount = linkedDoses;
            UnlinkedObjectCount = unlinkedStructs + unlinkedDoses;
            RtStructText = DescribeLinkedCount(linkedStructs, unlinkedStructs);
            RtDoseText = DescribeLinkedCount(linkedDoses, unlinkedDoses);

            // Plans and any other non-image series: linked to nothing and exported by nothing, but
            // part of what the folder holds.
            var others = _contents.SelectMany(c => c.Objects).Where(o => !StudyContents.IsLinkable(o)).ToList();
            OtherObjectsText = others.Count == 0
                ? "none"
                : string.Join(", ", others
                    .GroupBy(o => string.IsNullOrEmpty(o.Modality) ? Missing : o.Modality)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => string.Format(CultureInfo.InvariantCulture, "{0} {1}", g.Count(), g.Key)));

            Studies = studies.Select(s => new PatientStudyRow(s.Key, s.Value)).ToList();
        }

        public DicomPatientGroup Model { get; }

        public override string Header => "Patient inspector";

        /// <summary>The tree row's label, "PAT001 - Doe^Jane".</summary>
        public string Title => PatientName == Missing ? PatientId : $"{PatientId} - {PatientName}";

        public string PatientId { get; }
        public string PatientName { get; }

        public int StudyCount => Studies.Count;

        public int ImageSeriesCount { get; }

        /// <summary>"3 CT, 1 MR", or "none".</summary>
        public string ImageSeriesSummary { get; }

        /// <summary>Files in the patient's image series.</summary>
        public int ImageInstanceCount { get; }

        /// <summary>Structure sets / doses some image series of their study is linked to.</summary>
        public int LinkedRtStructCount { get; }
        public int LinkedRtDoseCount { get; }

        /// <summary>"2 linked", "2 linked, 1 unlinked", "1 unlinked", or "none".</summary>
        public string RtStructText { get; }
        public string RtDoseText { get; }

        /// <summary>"1 RTPLAN", or "none": the non-image series the scan neither links nor exports.</summary>
        public string OtherObjectsText { get; }

        /// <summary>The patient row's "non-uniform Z" count.</summary>
        public int NonUniformSeriesCount { get; }
        public bool HasNonUniformSeries => NonUniformSeriesCount > 0;
        public string NonUniformBadgeText => NonUniformSeriesCount == 1
            ? "non-uniform Z"
            : $"{NonUniformSeriesCount} non-uniform Z";

        /// <summary>The patient row's "link guessed" count.</summary>
        public int GuessedLinkSeriesCount { get; }
        public bool HasGuessedLinkSeries => GuessedLinkSeriesCount > 0;
        public string GuessedLinkBadgeText => GuessedLinkSeriesCount == 1
            ? "link guessed"
            : $"{GuessedLinkSeriesCount} links guessed";

        /// <summary>RTSTRUCT and RTDOSE objects linked to no image series of their study.</summary>
        public int UnlinkedObjectCount { get; }
        public bool HasUnlinkedObjects => UnlinkedObjectCount > 0;
        public string UnlinkedBadgeText => UnlinkedObjectCount == 1 ? "unlinked" : $"{UnlinkedObjectCount} unlinked";
        public string UnlinkedObjectsNote => StudyInspectorViewModel.UnlinkedNote;

        public IReadOnlyList<PatientStudyRow> Studies { get; }

        protected override string NoTagsText => "No curated patient tags present in the first slice of this patient's first image series.";

        protected override IReadOnlyList<InspectorTagSource> TagSources()
        {
            // The first image slice of any study; only a patient with no image series at all falls
            // back to the first file of an RT object.
            string path = _contents.SelectMany(c => c.ImageSeries).Select(FirstFile).FirstOrDefault(p => p != null)
                          ?? _contents.SelectMany(c => c.Objects).Select(FirstFile).FirstOrDefault(p => p != null);
            return new[] { new InspectorTagSource("Patient", path, PatientKeywords) };
        }

        private static string DescribeLinkedCount(int linked, int unlinked)
        {
            if (linked == 0 && unlinked == 0)
                return "none";
            if (unlinked == 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} linked", linked);
            if (linked == 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} unlinked", unlinked);
            return string.Format(CultureInfo.InvariantCulture, "{0} linked, {1} unlinked", linked, unlinked);
        }
    }

    /// <summary>One study of the inspected patient.</summary>
    public class PatientStudyRow
    {
        internal PatientStudyRow(DicomStudyGroup study, StudyContents contents)
        {
            DateText = InspectorViewModel.FormatDicomDate(study.StudyDate);
            Description = string.IsNullOrWhiteSpace(study.StudyDescription)
                ? "(no study description)"
                : study.StudyDescription.Trim();
            ImageSeriesCount = contents.ImageSeries.Count;
            RtObjectCount = contents.Objects.Count;
            UnlinkedObjectCount = contents.Objects.Count(contents.IsUnlinked);
            Summary = string.Format(CultureInfo.InvariantCulture, "{0} image series · {1} RT object(s)",
                ImageSeriesCount, RtObjectCount);
        }

        /// <summary>StudyDate as "2024-03-05", or <see cref="InspectorViewModel.Missing"/>.</summary>
        public string DateText { get; }
        public string Description { get; }
        public int ImageSeriesCount { get; }

        /// <summary>Every non-image series of the study, as the study inspector lists them.</summary>
        public int RtObjectCount { get; }

        /// <summary>"2 image series · 1 RT object(s)".</summary>
        public string Summary { get; }

        public int UnlinkedObjectCount { get; }
        public bool HasUnlinkedObjects => UnlinkedObjectCount > 0;
        public string UnlinkedBadgeText => UnlinkedObjectCount == 1 ? "unlinked" : $"{UnlinkedObjectCount} unlinked";
    }
}
