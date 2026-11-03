using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// The series inspector: what the scan already knows about one image series, laid out for a
    /// person deciding whether to export it. Everything above the tag grid is a pure mapping of
    /// the scanned <see cref="DicomSeriesGroup"/> through <see cref="SeriesGeometryProbe"/>, with no
    /// file access, so it is unit-tested from in-memory models. The tag grid is the one part that
    /// reads files (the series' first slice and its first linked RTSTRUCT / RTDOSE, headers only);
    /// it loads off the UI thread the first time the series is inspected.
    ///
    /// The non-uniform-Z badge is raised by exactly the test the conversions use to decide whether
    /// to warn (<see cref="SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(DicomSeriesGroup, out string)"/>),
    /// so the inspector, Convert Selected and Export Manifest Only can never disagree about a series.
    /// </summary>
    public class SeriesInspectorViewModel : InspectorViewModel
    {
        public SeriesInspectorViewModel(DicomSeriesGroup model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));

            Modality = string.IsNullOrEmpty(model.Modality) ? Missing : model.Modality;
            Title = string.IsNullOrWhiteSpace(model.SeriesDescription)
                ? "(no series description)"
                : model.SeriesDescription.Trim();
            SeriesInstanceUid = string.IsNullOrEmpty(model.SeriesInstanceUID) ? Missing : model.SeriesInstanceUID;
            SeriesDateText = FormatDicomDate(model.SeriesDate);
            FrameOfReferenceUid = string.IsNullOrEmpty(model.FrameOfReferenceUID) ? Missing : model.FrameOfReferenceUID;
            InstanceCount = model.FilePaths?.Count ?? 0;

            SeriesGeometry geometry;
            HasGeometry = SeriesGeometryProbe.TryDescribe(model, out geometry);
            if (HasGeometry)
            {
                SliceSpacingText = FormatMm(geometry.SliceSpacing);
                DistinctGapsText = geometry.DistinctGaps.Length == 0
                    ? Missing
                    : string.Join(", ", geometry.DistinctGaps.Select(FormatNumber)) + " mm";
                ExtentText = model.SlicePositions.Count < 2
                    ? Missing
                    : string.Format(CultureInfo.InvariantCulture, "{0} mm (z {1} to {2})",
                        FormatNumber(geometry.ExtentMm),
                        FormatNumber(model.SlicePositions.Min()),
                        FormatNumber(model.SlicePositions.Max()));
            }
            else
            {
                SliceSpacingText = Missing;
                DistinctGapsText = Missing;
                ExtentText = Missing;
            }
            PixelSpacingText = model.PixelSpacing != null && model.PixelSpacing.Length >= 2
                ? string.Format(CultureInfo.InvariantCulture, "{0} × {1} mm",
                    FormatNumber(model.PixelSpacing[0]), FormatNumber(model.PixelSpacing[1]))
                : Missing;

            string warning;
            IsNonUniformZ = SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(model, out warning);
            NonUniformZWarning = warning ?? "";

            // Gaps that differ only by rounding or jitter (0.62 / 0.63 mm on a 0.625 mm grid) are
            // non-uniform by the probe's absolute test but below the factor at which a conversion
            // warns. Say so, rather than leaving two listed gaps unexplained next to no badge.
            GapsWithinTolerance = HasGeometry && !geometry.Uniform && !IsNonUniformZ;

            string missingPosition;
            UnpositionedSliceCount = model.UnpositionedSlices?.Count ?? 0;
            MissingPositionWarning = SeriesGeometryProbe.TryBuildMissingPositionWarning(model, out missingPosition)
                ? missingPosition
                : "";

            var structs = LinkedOrSingle(model.LinkedRtStructs, model.LinkedRtStruct);
            var doses = LinkedOrSingle(model.LinkedRtDoses, model.LinkedRtDose);
            LinkedRtStructCount = structs.Count;
            LinkedRtDoseCount = doses.Count;
            RtStructLinkText = DescribeLinks(structs);
            RtDoseLinkText = DescribeLinks(doses);
            HasGuessedLink = structs.Concat(doses).Any(s => s.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);

            LinkedObjects = structs.Select(s => new InspectorLinkRow(
                    "RTSTRUCT", DescribeObject(s),
                    string.Format(CultureInfo.InvariantCulture, "{0} ROI(s)", s.RoiNames?.Count ?? 0),
                    LinkRuleLabel(s.LinkMatchRule), s.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback))
                .Concat(doses.Select(d => new InspectorLinkRow(
                    "RTDOSE", DescribeObject(d),
                    string.Format(CultureInfo.InvariantCulture, "{0} file(s)", d.FilePaths?.Count ?? 0),
                    LinkRuleLabel(d.LinkMatchRule), d.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback)))
                .ToList();
        }

        public DicomSeriesGroup Model { get; }

        public override string Header => "Series inspector";

        public string Title { get; }
        public string Modality { get; }
        public string SeriesInstanceUid { get; }

        /// <summary>SeriesDate as "2024-03-05", or <see cref="InspectorViewModel.Missing"/>.</summary>
        public string SeriesDateText { get; }

        public int InstanceCount { get; }

        /// <summary>False for a series whose slices carried no usable position.</summary>
        public bool HasGeometry { get; }

        /// <summary>Median slice gap, "2.5 mm"; the number a reader assuming a regular grid would not report.</summary>
        public string SliceSpacingText { get; }

        /// <summary>Every distinct slice gap, ascending, "1, 3 mm". One entry means a regular grid.</summary>
        public string DistinctGapsText { get; }

        public string PixelSpacingText { get; }

        /// <summary>First-to-last slice distance with the z range, "45 mm (z -20 to 25)".</summary>
        public string ExtentText { get; }

        public string FrameOfReferenceUid { get; }

        /// <summary>True exactly when Convert Selected and Export Manifest Only would warn about this series.</summary>
        public bool IsNonUniformZ { get; }

        /// <summary>The warning those conversions log, verbatim; empty when <see cref="IsNonUniformZ"/> is false.</summary>
        public string NonUniformZWarning { get; }

        /// <summary>Gaps differ, but only by rounding: no badge, and the inspector says why.</summary>
        public bool GapsWithinTolerance { get; }

        public int UnpositionedSliceCount { get; }

        /// <summary>The conversions' missing-position warning, verbatim; empty on a clean series.</summary>
        public string MissingPositionWarning { get; }
        public bool HasMissingPositions => MissingPositionWarning.Length > 0;

        public int LinkedRtStructCount { get; }
        public int LinkedRtDoseCount { get; }

        /// <summary>"1 linked, by referenced series UID", or "none".</summary>
        public string RtStructLinkText { get; }
        public string RtDoseLinkText { get; }

        /// <summary>
        /// True when any linked RT object was attached by the largest-series fallback: nothing
        /// identified the image series, so the link is a guess.
        /// </summary>
        public bool HasGuessedLink { get; }

        public IReadOnlyList<InspectorLinkRow> LinkedObjects { get; }
        public bool HasLinkedObjects => LinkedObjects.Count > 0;

        protected override string NoTagsText => "No curated tags present in this series' files.";

        protected override IReadOnlyList<InspectorTagSource> TagSources() => new[]
        {
            new InspectorTagSource("Image", FirstFile(Model), MetadataTagCatalog.CuratedImageKeywords),
            new InspectorTagSource("Structure set",
                FirstFile(LinkedOrSingle(Model.LinkedRtStructs, Model.LinkedRtStruct).FirstOrDefault()),
                MetadataTagCatalog.CuratedStructureKeywords),
            new InspectorTagSource("Dose",
                FirstFile(LinkedOrSingle(Model.LinkedRtDoses, Model.LinkedRtDose).FirstOrDefault()),
                MetadataTagCatalog.CuratedDoseKeywords),
        };

        /// <summary>
        /// "none", "1 linked, by referenced series UID", or, when the linked objects were matched by
        /// different rules, "2 linked: 1 by referenced series UID, 1 by frame of reference UID".
        /// </summary>
        internal static string DescribeLinks(IReadOnlyList<DicomSeriesGroup> linked)
        {
            if (linked == null || linked.Count == 0)
                return "none";

            var byRule = linked
                .GroupBy(l => l.LinkMatchRule)
                .OrderBy(g => (int)g.Key == 0 ? int.MaxValue : (int)g.Key)
                .ToList();

            if (byRule.Count == 1)
                return string.Format(CultureInfo.InvariantCulture, "{0} linked, by {1}",
                    linked.Count, LinkRuleLabel(byRule[0].Key));

            return string.Format(CultureInfo.InvariantCulture, "{0} linked: {1}",
                linked.Count,
                string.Join(", ", byRule.Select(g => string.Format(CultureInfo.InvariantCulture,
                    "{0} by {1}", g.Count(), LinkRuleLabel(g.Key)))));
        }

        private static string FormatMm(double? value)
            => value.HasValue ? FormatNumber(value.Value) + " mm" : Missing;
    }

    /// <summary>One RT object linked to the inspected series, and the rule that linked it.</summary>
    public class InspectorLinkRow
    {
        public InspectorLinkRow(string kind, string description, string detail, string rule, bool isGuess)
        {
            Kind = kind;
            Description = description;
            Detail = detail;
            Rule = rule;
            IsGuess = isGuess;
        }

        public string Kind { get; }
        public string Description { get; }
        public string Detail { get; }
        public string Rule { get; }
        public bool IsGuess { get; }
    }
}
