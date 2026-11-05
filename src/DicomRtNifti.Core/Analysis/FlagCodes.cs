using System.Collections.Generic;
using System.Linq;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// How much a flag matters. <see cref="Serious"/>: the output is wrong unless someone acts;
    /// <see cref="Warning"/>: a guess, a loss or a choice the export makes silently, worth a look;
    /// <see cref="Info"/>: a fact a curator may want, nothing to fix.
    /// </summary>
    public static class FlagSeverity
    {
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Serious = "serious";
    }

    /// <summary>One flag code: what raises it, and how much it matters.</summary>
    public sealed class FlagDefinition
    {
        internal FlagDefinition(string code, string severity, string tier, string meaning)
        {
            Code = code;
            Severity = severity;
            Tier = tier;
            Meaning = meaning;
        }

        public string Code { get; }

        /// <summary>One of <see cref="FlagSeverity"/>'s values; fixed per code.</summary>
        public string Severity { get; }

        /// <summary>The cheapest tier that raises it (<see cref="AnalysisTierNames"/>).</summary>
        public string Tier { get; }

        /// <summary>One sentence, as README's flag table gives it.</summary>
        public string Meaning { get; }
    }

    /// <summary>
    /// The fixed, documented list of flag codes the cohort analysis raises
    /// (<c>dicomrtnifti.cohort-analysis/1</c>). A code is a contract: scripts filter on it, so a
    /// code is never renamed or reused, and a new one is added here, in README's table and in a
    /// test of its own. Codes the contours and raster tiers will raise (ROI_EMPTY,
    /// ROI_OFF_PLANE, DOSE_NOT_ALIGNED, ...) are added by the briefs that build those tiers.
    ///
    /// The flags that mirror a desktop-app badge use the badge's own predicate
    /// (<see cref="SeriesFlags"/>), so the web, the GUI and the CLI can never disagree about a
    /// series: GEOM_NONUNIFORM_Z ("non-uniform Z"), GEOM_UNPOSITIONED (the series inspector's
    /// missing-position badge), LINK_FALLBACK ("link guessed"), LINK_UNLINKED ("unlinked"),
    /// LINK_MULTIPLE_FRAMES (the study inspector's frames note) and ROI_UNASSOCIATED (the ROI
    /// association browser's unmatched-name warning).
    /// </summary>
    public static class FlagCodes
    {
        public const string GeomNonUniformZ = "GEOM_NONUNIFORM_Z";
        public const string GeomUnpositioned = "GEOM_UNPOSITIONED";
        public const string GeomOblique = "GEOM_OBLIQUE";
        public const string GeomMixedOrientation = "GEOM_MIXED_ORIENTATION";
        public const string GeomModalityMinority = "GEOM_MODALITY_MINORITY";
        public const string LinkFallback = "LINK_FALLBACK";
        public const string LinkUnlinked = "LINK_UNLINKED";
        public const string LinkForMismatch = "LINK_FOR_MISMATCH";
        public const string LinkReferencedSeriesAbsent = "LINK_REFERENCED_SERIES_ABSENT";
        public const string LinkMultipleFrames = "LINK_MULTIPLE_FRAMES";
        public const string RtMultipleStructs = "RT_MULTIPLE_STRUCTS";
        public const string RoiDuplicateName = "ROI_DUPLICATE_NAME";
        public const string RoiUnassociated = "ROI_UNASSOCIATED";

        /// <summary>Every code, in the order a series' flag list and README's table give them.</summary>
        public static IReadOnlyList<FlagDefinition> All { get; } = new[]
        {
            new FlagDefinition(GeomNonUniformZ, FlagSeverity.Serious, AnalysisTierNames.Header,
                "Slice gaps differ by enough that a volume computed from the written (averaged) spacing is off by 5% or more; the conversion warning, verbatim."),
            new FlagDefinition(GeomUnpositioned, FlagSeverity.Warning, AnalysisTierNames.Header,
                "Image slices without a usable ImagePositionPatient were left out of the series; the conversion warning, verbatim."),
            new FlagDefinition(GeomOblique, FlagSeverity.Warning, AnalysisTierNames.Header,
                "The slice normal is not the patient z axis (gantry tilt, oblique, coronal or sagittal stack): slice spacing is measured along z and does not describe the series, and contours are matched to slices by z (toolkit issue 44)."),
            new FlagDefinition(GeomMixedOrientation, FlagSeverity.Serious, AnalysisTierNames.Header,
                "The series' files carry different ImageOrientationPatient values, so they are not one stack of parallel slices."),
            new FlagDefinition(GeomModalityMinority, FlagSeverity.Info, AnalysisTierNames.Header,
                "Some instances carry a Modality other than the series' majority (a stray localizer or secondary capture)."),
            new FlagDefinition(LinkFallback, FlagSeverity.Warning, AnalysisTierNames.Header,
                "An RTSTRUCT or RTDOSE named neither this series nor its frame of reference, and was attached to the study's largest image series: a guess."),
            new FlagDefinition(LinkUnlinked, FlagSeverity.Warning, AnalysisTierNames.Header,
                "An RTSTRUCT or RTDOSE is linked to no image series in its study, so no export includes it."),
            new FlagDefinition(LinkForMismatch, FlagSeverity.Serious, AnalysisTierNames.Header,
                "An RTSTRUCT or RTDOSE references this series by UID, but none of its frames of reference is the series' own."),
            new FlagDefinition(LinkReferencedSeriesAbsent, FlagSeverity.Warning, AnalysisTierNames.Header,
                "A structure set references an image series its study does not hold (not exported, or in another study), so it was linked by a weaker rule or not at all."),
            new FlagDefinition(LinkMultipleFrames, FlagSeverity.Info, AnalysisTierNames.Header,
                "The image series of a study span several frames of reference; a structure set or dose applies to the series of its own frame only."),
            new FlagDefinition(RtMultipleStructs, FlagSeverity.Warning, AnalysisTierNames.Header,
                "Several structure sets are linked to one image series; an export takes one (--struct-series, then --struct-description, then the first linked)."),
            new FlagDefinition(RoiDuplicateName, FlagSeverity.Warning, AnalysisTierNames.Header,
                "A structure set lists one ROI name more than once; an export keeps the name for the lowest ROINumber and suffixes the others."),
            new FlagDefinition(RoiUnassociated, FlagSeverity.Info, AnalysisTierNames.Header,
                "With an association set: an ROI name no association covers. --only-associated-rois drops it; without that flag it is exported under its own name."),
        };

        private static readonly Dictionary<string, FlagDefinition> ByCode = All.ToDictionary(d => d.Code);

        /// <summary>The definition of <paramref name="code"/>; throws for a code not in <see cref="All"/>.</summary>
        public static FlagDefinition Get(string code) => ByCode[code];

        /// <summary>Position of <paramref name="code"/> in <see cref="All"/>, for ordering.</summary>
        internal static int OrderOf(string code)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Code == code) return i;
            }
            return All.Count;
        }
    }
}
