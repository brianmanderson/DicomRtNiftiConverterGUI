using System.Collections.Generic;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Analysis
{
    // The result of a cohort analysis. These types are the document's shape: the document
    // serializes them as they are, through the cohort documents' snake_case serializer, so a
    // property added here is a field added to dicomrtnifti.cohort-analysis/1, and its golden
    // records it. Every identifier in them is already presented (hashed under anonymization) by
    // CohortAnalysisService; nothing here is a path.

    /// <summary>A whole cohort, analysed at one tier.</summary>
    public sealed class CohortAnalysis
    {
        /// <summary>The tier computed (<see cref="AnalysisTierNames"/>).</summary>
        public string Tier { get; set; }

        public bool Anonymized { get; set; }

        /// <summary>The scanned folder, full; null when anonymized. The document presents it.</summary>
        [JsonIgnore]
        public string InputRoot { get; set; }

        /// <summary>The name of the association set ROI names were resolved against, or null for none.</summary>
        public string Associations { get; set; }

        public CohortAnalysisSummary Summary { get; set; } = new CohortAnalysisSummary();

        public GeometrySummary Geometry { get; set; } = new GeometrySummary();

        /// <summary>Every image series (CT, MR, PT), in scan order: patient, study, series.</summary>
        public List<SeriesAnalysis> Series { get; set; } = new List<SeriesAnalysis>();

        public RoiNamingSummary RoiNaming { get; set; } = new RoiNamingSummary();

        public LinkageSummary Linkage { get; set; } = new LinkageSummary();

        /// <summary>Every flag raised, in scan order; the series' own lists name the same codes.</summary>
        public List<AnalysisFlag> Flags { get; set; } = new List<AnalysisFlag>();
    }

    /// <summary>The cohort in counts.</summary>
    public sealed class CohortAnalysisSummary
    {
        public int Patients { get; set; }
        public int Studies { get; set; }
        public int ImageSeries { get; set; }
        public int Rtstructs { get; set; }
        public int Rtdoses { get; set; }

        /// <summary>Series that are neither image series nor RT objects the scan links (RTPLAN, REG, SR, ...).</summary>
        public int OtherObjects { get; set; }

        /// <summary>Flags raised, by severity: serious, warning, info, every one present.</summary>
        public Dictionary<string, int> FlagsBySeverity { get; set; } = new Dictionary<string, int>();

        /// <summary>Flags raised, by code, every code of <see cref="FlagCodes.All"/> present in its order.</summary>
        public Dictionary<string, int> FlagsByCode { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>One image series and everything the header tier knows about it.</summary>
    public sealed class SeriesAnalysis
    {
        public string PatientId { get; set; }
        public string StudyUid { get; set; }
        public string SeriesUid { get; set; }
        public string Modality { get; set; }
        public string SeriesDescription { get; set; }
        public SeriesGeometryFacts Geometry { get; set; }

        /// <summary>Null when no file carries a usable ImageOrientationPatient, or the files disagree (GEOM_MIXED_ORIENTATION).</summary>
        public SeriesOrientation Orientation { get; set; }

        public SeriesHeaderFacts Header { get; set; }
        public SeriesLinks Links { get; set; }

        /// <summary>Every ROI of every linked structure set, set by set in link order, ROIs in file order.</summary>
        public List<RoiEntry> Rois { get; set; } = new List<RoiEntry>();

        /// <summary>Every linked RTDOSE, in link order.</summary>
        public List<LinkedDose> Dose { get; set; } = new List<LinkedDose>();

        /// <summary>The codes raised on this series, each once, in <see cref="FlagCodes.All"/> order.</summary>
        public List<string> Flags { get; set; } = new List<string>();
    }

    /// <summary>The voxel grid of one image series, from the scanned headers.</summary>
    public sealed class SeriesGeometryFacts
    {
        /// <summary>Files placed in the series (positioned slices, or whole multi-frame objects).</summary>
        public int InstanceCount { get; set; }

        /// <summary>Slices with a usable ImagePositionPatient: the positions the gaps are measured between.</summary>
        public int SliceCount { get; set; }

        /// <summary>Image slices left out for lacking a usable ImagePositionPatient (GEOM_UNPOSITIONED).</summary>
        public int UnpositionedSlices { get; set; }

        /// <summary>PixelSpacing as DICOM orders it: [row, column], that is [y, x], in mm.</summary>
        public double[] PixelSpacing { get; set; }

        /// <summary>[x, y, z] in mm: column spacing, row spacing, median slice gap.</summary>
        public double[] Spacing { get; set; }

        /// <summary>Median gap between consecutive slices, mm.</summary>
        public double? SliceSpacing { get; set; }

        public bool? SliceSpacingUniform { get; set; }

        /// <summary>The distinct slice gaps, ascending, mm.</summary>
        public double[] SliceSpacingValues { get; set; }

        /// <summary>
        /// Worst-case factor a volume computed from the written (averaged) spacing is off by: 1
        /// for a regular grid, 1.05 or more raises GEOM_NONUNIFORM_Z. Null with fewer than two slices.
        /// </summary>
        public double? SpacingErrorFactor { get; set; }

        public double? ZMin { get; set; }
        public double? ZMax { get; set; }

        /// <summary>First to last slice, mm.</summary>
        public double? ExtentMm { get; set; }

        public double? SliceThickness { get; set; }
        public int? Rows { get; set; }
        public int? Columns { get; set; }

        /// <summary>In-plane field of view [x, y] in mm: Columns × column spacing, Rows × row spacing.</summary>
        public double[] FieldOfViewMm { get; set; }
    }

    /// <summary>Header values of one image series that are not its grid.</summary>
    public sealed class SeriesHeaderFacts
    {
        /// <summary>The series' RescaleSlope, or null when absent or when the files disagree (<see cref="RescaleVaries"/>).</summary>
        public double? RescaleSlope { get; set; }

        public double? RescaleIntercept { get; set; }

        /// <summary>The files carry different rescales, as PET series that scale each slice do.</summary>
        public bool RescaleVaries { get; set; }

        /// <summary>NumberOfFrames when the series is multi-frame, else null.</summary>
        public int? NumberOfFrames { get; set; }

        /// <summary>Instances per Modality value; more than one entry raises GEOM_MODALITY_MINORITY.</summary>
        public Dictionary<string, int> ModalityCounts { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>The structure sets linked to one image series.</summary>
    public sealed class SeriesLinks
    {
        public List<LinkedStructureSet> Rtstructs { get; set; } = new List<LinkedStructureSet>();

        /// <summary>The structure set an export with no --struct-series or --struct-description takes: the first linked. Null for none.</summary>
        public string DefaultRtstruct { get; set; }
    }

    public sealed class LinkedStructureSet
    {
        public string SeriesUid { get; set; }
        public string SeriesDescription { get; set; }

        /// <summary><see cref="Models.RtLinkMatchRule"/>, by name.</summary>
        public string MatchRule { get; set; }

        public int RoiCount { get; set; }

        /// <summary>
        /// Whether the image's frame of reference is one the structure set names; null when either
        /// side names none. False on a link by referenced series raises LINK_FOR_MISMATCH.
        /// </summary>
        public bool? FrameOfReferenceMatch { get; set; }
    }

    public sealed class LinkedDose
    {
        public string SeriesUid { get; set; }
        public string SeriesDescription { get; set; }
        public string MatchRule { get; set; }
        public int FileCount { get; set; }

        /// <summary>How many image series the dose is linked to: more than one when they share its frame of reference.</summary>
        public int LinkedSeries { get; set; }

        public bool? FrameOfReferenceMatch { get; set; }
    }

    /// <summary>One ROI of one linked structure set.</summary>
    public sealed class RoiEntry
    {
        public string StructureSetUid { get; set; }
        public string Name { get; set; }

        /// <summary>The canonical name the associations give it (<see cref="Models.RoiNameMatcher.ResolveToCanonical"/>), or its own name.</summary>
        public string CanonicalName { get; set; }

        /// <summary>An association covers it; false for every ROI when no association set is given.</summary>
        public bool Associated { get; set; }

        /// <summary>The mask name an export with these associations writes it under, or null when the export drops it.</summary>
        public string OutputName { get; set; }

        /// <summary>Whether --only-associated-rois keeps it (the export's own resolver; with no associations it keeps everything).</summary>
        public bool KeptIfOnlyAssociated { get; set; }

        /// <summary>The structure set names another ROI the same (ROI_DUPLICATE_NAME).</summary>
        public bool Duplicate { get; set; }
    }

    /// <summary>One flag: a code from <see cref="FlagCodes"/> and where it was raised.</summary>
    public sealed class AnalysisFlag
    {
        public string Code { get; set; }
        public string Severity { get; set; }
        public string PatientId { get; set; }
        public string StudyUid { get; set; }

        /// <summary>The image series it concerns, or null for a study- or cohort-level flag and an unlinked object.</summary>
        public string SeriesUid { get; set; }

        /// <summary>The RT object it concerns (an RTSTRUCT or RTDOSE series), or null.</summary>
        public string ObjectUid { get; set; }

        public string Roi { get; set; }

        /// <summary>
        /// What was found, in the words Core already uses where it has some (the conversion
        /// warnings verbatim), so the web, the GUI log and stderr say the same thing.
        /// </summary>
        public string Message { get; set; }
    }

    /// <summary>Image geometry across the cohort.</summary>
    public sealed class GeometrySummary
    {
        public int ImageSeries { get; set; }

        /// <summary>Image series with at least one positioned slice.</summary>
        public int WithPositions { get; set; }

        public int Uniform { get; set; }

        /// <summary>Gaps differ, but only by rounding: no warning (the inspector's "within tolerance").</summary>
        public int NonUniformWithinTolerance { get; set; }

        /// <summary>GEOM_NONUNIFORM_Z.</summary>
        public int NonUniformMaterial { get; set; }

        public int WithUnpositionedSlices { get; set; }

        /// <summary>Image series per <see cref="SeriesOrientation.Plane"/>, plus "unknown" for no or mixed orientation; every key present.</summary>
        public Dictionary<string, int> Planes { get; set; } = new Dictionary<string, int>();

        /// <summary>Column spacing (x), mm.</summary>
        public Distribution PixelSpacingXMm { get; set; } = new Distribution();

        /// <summary>Row spacing (y), mm.</summary>
        public Distribution PixelSpacingYMm { get; set; } = new Distribution();

        /// <summary>Median slice gap, mm.</summary>
        public Distribution SliceSpacingMm { get; set; } = new Distribution();

        /// <summary>Positioned slices per series.</summary>
        public Distribution SliceCount { get; set; } = new Distribution();

        public Distribution ExtentZMm { get; set; } = new Distribution();

        /// <summary>Series per in-plane grid, most common first.</summary>
        public List<GridCount> Grids { get; set; } = new List<GridCount>();
    }

    /// <summary>
    /// One measure across the series that have it: its range and median, and how many series
    /// have each value (rounded to 4 decimals), ascending. The histogram a page draws is binned
    /// from <see cref="Values"/>.
    /// </summary>
    public sealed class Distribution
    {
        public int Series { get; set; }
        public double? Min { get; set; }
        public double? Median { get; set; }
        public double? Max { get; set; }
        public List<ValueCount> Values { get; set; } = new List<ValueCount>();
    }

    public sealed class ValueCount
    {
        public double Value { get; set; }
        public int Series { get; set; }
    }

    public sealed class GridCount
    {
        public int? Rows { get; set; }
        public int? Columns { get; set; }
        public int Series { get; set; }
    }

    /// <summary>
    /// ROI names across the cohort, counted the way the desktop app's ROI browser and Select ROIs
    /// count them: over each image series' default structure set (its first linked), names
    /// compared ignoring case, the first spelling met kept.
    /// </summary>
    public sealed class RoiNamingSummary
    {
        /// <summary>Every name, ascending ignoring case.</summary>
        public List<RoiNameEntry> Names { get; set; } = new List<RoiNameEntry>();

        /// <summary>Names <see cref="Models.RoiNameMatcher.Normalize"/> makes equal: alias suggestions, never applied.</summary>
        public List<RoiNameCluster> Clusters { get; set; } = new List<RoiNameCluster>();

        public RoiCoverage Coverage { get; set; } = new RoiCoverage();

        /// <summary>With an association set: the names no association covers. Empty without one.</summary>
        public List<string> Uncovered { get; set; } = new List<string>();

        /// <summary>The names --only-associated-rois drops from the default structure sets, with how many series lose each.</summary>
        public List<RoiNameCount> DroppedIfOnlyAssociated { get; set; } = new List<RoiNameCount>();
    }

    public sealed class RoiNameEntry
    {
        public string Name { get; set; }

        /// <summary>Image series whose default structure set has it: the desktop app's count.</summary>
        public int Series { get; set; }

        /// <summary>Patients whose default structure sets have it.</summary>
        public int Patients { get; set; }

        /// <summary>Every RTSTRUCT in the scan that has it, linked, default or not.</summary>
        public int StructureSets { get; set; }

        /// <summary><see cref="Models.RoiNameMatcher.Normalize"/> of the name.</summary>
        public string Normalized { get; set; }

        public string CanonicalName { get; set; }
        public bool Associated { get; set; }
    }

    public sealed class RoiNameCluster
    {
        public string Normalized { get; set; }
        public List<string> Names { get; set; } = new List<string>();
        public int Series { get; set; }
    }

    public sealed class RoiNameCount
    {
        public string Name { get; set; }
        public int Series { get; set; }
    }

    /// <summary>
    /// Canonical names per patient, exactly as Select ROIs counts them: each default-set name
    /// resolved to its canonical name (or itself), a patient counted once per canonical name, out
    /// of the patients with any ROI.
    /// </summary>
    public sealed class RoiCoverage
    {
        public int PatientsWithRois { get; set; }
        public List<CanonicalCoverage> Canonical { get; set; } = new List<CanonicalCoverage>();

        /// <summary>The patient × canonical-name matrix, a row per patient with ROIs, in scan order.</summary>
        public List<PatientCoverage> Patients { get; set; } = new List<PatientCoverage>();
    }

    public sealed class CanonicalCoverage
    {
        public string CanonicalName { get; set; }

        /// <summary>The name is an association's canonical name, not a raw name standing for itself.</summary>
        public bool FromAssociation { get; set; }

        public int Patients { get; set; }

        /// <summary>Patients / <see cref="RoiCoverage.PatientsWithRois"/>, to 4 decimals.</summary>
        public double Share { get; set; }
    }

    public sealed class PatientCoverage
    {
        public string PatientId { get; set; }
        public List<string> CanonicalNames { get; set; } = new List<string>();
    }

    /// <summary>How every RT object was linked, and what the links leave to look at.</summary>
    public sealed class LinkageSummary
    {
        /// <summary>RTSTRUCT and RTDOSE objects per link rule, every rule present.</summary>
        public Dictionary<string, Dictionary<string, int>> RuleCounts { get; set; } = new Dictionary<string, Dictionary<string, int>>();

        /// <summary>Objects linked by LargestSeriesFallback (LINK_FALLBACK).</summary>
        public List<LinkageObject> Fallback { get; set; } = new List<LinkageObject>();

        /// <summary>RTSTRUCT and RTDOSE objects linked to nothing (LINK_UNLINKED), with the reason.</summary>
        public List<LinkageObject> Unlinked { get; set; } = new List<LinkageObject>();

        /// <summary>Links by referenced series whose frames of reference disagree (LINK_FOR_MISMATCH).</summary>
        public List<LinkageObject> ForMismatch { get; set; } = new List<LinkageObject>();

        /// <summary>Structure sets referencing an image series their study does not hold (LINK_REFERENCED_SERIES_ABSENT).</summary>
        public List<LinkageObject> ReferencedSeriesAbsent { get; set; } = new List<LinkageObject>();

        /// <summary>Image series with more than one linked structure set (RT_MULTIPLE_STRUCTS).</summary>
        public List<MultiStructSeries> MultiStructSeries { get; set; } = new List<MultiStructSeries>();

        /// <summary>Doses linked to more than one image series, which share their frame of reference.</summary>
        public List<LinkageObject> SharedDoses { get; set; } = new List<LinkageObject>();

        public int SeriesWithoutRtstruct { get; set; }
        public int SeriesWithoutRtdose { get; set; }

        /// <summary>Frames of reference among each study's image series (LINK_MULTIPLE_FRAMES past one).</summary>
        public List<StudyFrames> FramesPerStudy { get; set; } = new List<StudyFrames>();

        /// <summary>Series the scan neither treats as images nor links (RTPLAN, REG, SR, ...): listed, never exported.</summary>
        public List<LinkageObject> OtherObjects { get; set; } = new List<LinkageObject>();
    }

    public sealed class LinkageObject
    {
        public string PatientId { get; set; }
        public string StudyUid { get; set; }
        public string ObjectUid { get; set; }
        public string Modality { get; set; }
        public string SeriesDescription { get; set; }
        public string MatchRule { get; set; }

        /// <summary>The image series it is linked to.</summary>
        public List<string> LinkedTo { get; set; } = new List<string>();

        /// <summary>The image series a structure set references, where that is what the entry is about; else null.</summary>
        public string ReferencedSeries { get; set; }

        public string Reason { get; set; }
    }

    public sealed class MultiStructSeries
    {
        public string PatientId { get; set; }
        public string StudyUid { get; set; }
        public string SeriesUid { get; set; }
        public List<string> StructureSets { get; set; } = new List<string>();
        public string DefaultRtstruct { get; set; }
    }

    public sealed class StudyFrames
    {
        public string PatientId { get; set; }
        public string StudyUid { get; set; }
        public int ImageSeries { get; set; }
        public int Frames { get; set; }
        public int ImageSeriesWithoutFrame { get; set; }
    }
}
