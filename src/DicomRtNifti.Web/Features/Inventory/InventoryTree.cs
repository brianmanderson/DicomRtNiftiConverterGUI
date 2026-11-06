using System.Globalization;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Web.Features.Inventory;

/// <summary>
/// A badge on a tree row: a flag code of the header-tier analysis (<see cref="FlagCodes"/>), its
/// fixed severity, the GUI's word for it, and the conversions' warning where Core has one. Every
/// badge is raised by a <see cref="SeriesFlags"/> predicate; nothing here decides one.
/// </summary>
public sealed record InventoryBadge(string Code, string Label, string? Detail = null)
{
    public string Severity => FlagCodes.Get(Code).Severity;

    public string Meaning => FlagCodes.Get(Code).Meaning;
}

/// <summary>
/// The Inventory's Patient / Study / Series tree over one scanned snapshot: the image series the
/// tree lists (<see cref="SeriesFlags.IsImageSeries"/>), every other series of a study as an RT
/// object row, and the badges <see cref="SeriesFlags"/> raises. Built once per snapshot and read
/// only after that; filtering and paging read it, never rebuild it.
///
/// Node keys are positions in the tree ("p2.s0.i3"), not identifiers, so nothing a page keeps
/// in its state or markup to address a row carries a UID or a patient ID.
/// </summary>
public sealed class InventoryTree
{
    public InventoryTree(DicomScanResult scan)
    {
        Scan = scan ?? throw new ArgumentNullException(nameof(scan));
        Patients = scan.Patients.Select((p, i) => new PatientNode(p, "p" + i.ToString(CultureInfo.InvariantCulture))).ToList();
        ByKey = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var patient in Patients)
        {
            ByKey[patient.Key] = patient;
            foreach (var study in patient.Studies)
            {
                ByKey[study.Key] = study;
                foreach (var series in study.ImageSeries)
                    ByKey[series.Key] = series;
            }
        }

        SeriesCount = Patients.Sum(p => p.ImageSeries.Count());
        BadgeCodes = Patients
            .SelectMany(p => p.Studies)
            .SelectMany(s => s.Badges.Concat(s.ImageSeries.SelectMany(x => x.Badges)))
            .Select(b => b.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => FlagCodes.All.ToList().FindIndex(d => d.Code == c))
            .ToList();
    }

    public DicomScanResult Scan { get; }

    public IReadOnlyList<PatientNode> Patients { get; }

    /// <summary>Image series in the tree.</summary>
    public int SeriesCount { get; }

    /// <summary>The badge codes some row of this tree carries, in <see cref="FlagCodes.All"/> order: the filter's choices.</summary>
    public IReadOnlyList<string> BadgeCodes { get; }

    private Dictionary<string, object> ByKey { get; }

    /// <summary>The node with this key: a <see cref="PatientNode"/>, <see cref="StudyNode"/> or <see cref="SeriesNode"/>.</summary>
    public object? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var node) ? node : null;

    /// <summary>
    /// The rows to show, in tree order. With no filter, a study shows when its patient is
    /// expanded and a series when its study is; with a filter, every row on the way to a match
    /// shows, expanded.
    /// </summary>
    public List<InventoryRow> Rows(InventoryFilter filter, IReadOnlySet<string> expanded)
    {
        var rows = new List<InventoryRow>();
        foreach (var patient in Patients)
        {
            var studies = patient.Studies.Where(s => filter.Matches(s)).ToList();
            if (filter.IsActive && studies.Count == 0)
                continue;
            rows.Add(new InventoryRow(patient));
            if (!filter.IsActive && !expanded.Contains(patient.Key))
                continue;
            foreach (var study in filter.IsActive ? studies : patient.Studies)
            {
                rows.Add(new InventoryRow(study));
                if (!filter.IsActive && !expanded.Contains(study.Key))
                    continue;
                foreach (var series in study.ImageSeries.Where(s => !filter.IsActive || filter.Matches(s) || filter.MatchesOwn(study)))
                    rows.Add(new InventoryRow(series));
            }
        }

        return rows;
    }

    /// <summary>"3 CT, 1 MR" in CT, MR, PT order; "none" for no series. Display counts, not a Core rule.</summary>
    public static string DescribeModalityCounts(IEnumerable<SeriesNode> imageSeries)
    {
        string[] order = { "CT", "MR", "PT" };
        var counts = imageSeries
            .GroupBy(s => s.Series.Modality)
            .OrderBy(g => Array.IndexOf(order, g.Key))
            .Select(g => string.Format(CultureInfo.InvariantCulture, "{0} {1}", g.Count(), g.Key))
            .ToList();
        return counts.Count == 0 ? "none" : string.Join(", ", counts);
    }
}

/// <summary>One visible row of the tree.</summary>
public sealed record InventoryRow(object Node)
{
    public int Depth => Node switch { PatientNode => 0, StudyNode => 1, _ => 2 };

    public string Key => Node switch
    {
        PatientNode p => p.Key,
        StudyNode s => s.Key,
        SeriesNode x => x.Key,
        _ => "",
    };
}

/// <summary>
/// The Inventory's filters: free text over the names, descriptions and modalities a row shows,
/// and one badge code. Applied on the server, over the tree in memory.
/// </summary>
public sealed record InventoryFilter(string? Text = null, string? BadgeCode = null)
{
    public static readonly InventoryFilter None = new();

    public bool IsActive => !string.IsNullOrWhiteSpace(Text) || BadgeCode is not null;

    /// <summary>A series matches when the text matches it or its parents, and it carries the badge.</summary>
    public bool Matches(SeriesNode series) =>
        TextMatches(series.Patient, series.Study, series) && (BadgeCode is null || series.Badges.Any(b => b.Code == BadgeCode));

    /// <summary>A study matches through one of its series, or on its own text and badges.</summary>
    public bool Matches(StudyNode study) => !IsActive || study.ImageSeries.Any(Matches) || MatchesOwn(study);

    /// <summary>The study's own text and its study-level badges (unlinked objects, several frames).</summary>
    public bool MatchesOwn(StudyNode study) =>
        TextMatches(study.Patient, study, null) && (BadgeCode is null || study.Badges.Any(b => b.Code == BadgeCode));

    private bool TextMatches(PatientNode patient, StudyNode study, SeriesNode? series)
    {
        if (string.IsNullOrWhiteSpace(Text))
            return true;
        string text = Text.Trim();
        return Has(patient.Patient.PatientID) || Has(patient.Patient.PatientName)
            || Has(study.Study.StudyDescription)
            || (series is not null && (Has(series.Series.SeriesDescription) || Has(series.Series.Modality)));

        bool Has(string? value) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PatientNode
{
    internal PatientNode(DicomPatientGroup patient, string key)
    {
        Patient = patient;
        Key = key;
        Studies = patient.Studies.Select((s, i) => new StudyNode(this, s, key + ".s" + i.ToString(CultureInfo.InvariantCulture))).ToList();
    }

    public DicomPatientGroup Patient { get; }

    public string Key { get; }

    public IReadOnlyList<StudyNode> Studies { get; }

    public IEnumerable<SeriesNode> ImageSeries => Studies.SelectMany(s => s.ImageSeries);

    /// <summary>The tree row's label, "PAT001 - Doe^Jane", as the GUI writes it.</summary>
    public string Label => string.IsNullOrEmpty(Patient.PatientName)
        ? InventoryText.OrMissing(Patient.PatientID)
        : $"{InventoryText.OrMissing(Patient.PatientID)} - {Patient.PatientName}";

    /// <summary>The patient row's badges: its series' and studies' badges, counted.</summary>
    public IReadOnlyList<(InventoryBadge Badge, int Count)> BadgeCounts =>
        Studies.SelectMany(s => s.Badges.Concat(s.ImageSeries.SelectMany(x => x.Badges)))
            .GroupBy(b => b.Code)
            .Select(g => (g.First(), g.Count()))
            .OrderBy(b => FlagCodes.All.ToList().FindIndex(d => d.Code == b.Item1.Code))
            .ToList();
}

public sealed class StudyNode
{
    internal StudyNode(PatientNode patient, DicomStudyGroup study, string key)
    {
        Patient = patient;
        Study = study;
        Key = key;
        var series = study.Series ?? new List<DicomSeriesGroup>();
        ImageSeries = series.Where(SeriesFlags.IsImageSeries)
            .Select((s, i) => new SeriesNode(this, s, key + ".i" + i.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        Objects = series.Where(s => s != null && !SeriesFlags.IsImageSeries(s)).Select(o => new RtObjectRow(study, o)).ToList();

        var badges = new List<InventoryBadge>();
        int unlinked = Objects.Count(o => o.IsUnlinked);
        if (unlinked > 0)
            badges.Add(new InventoryBadge(FlagCodes.LinkUnlinked, unlinked == 1 ? "unlinked" : $"{unlinked} unlinked"));
        if (SeriesFlags.HasMultipleFrames(study, out int frames))
            badges.Add(new InventoryBadge(FlagCodes.LinkMultipleFrames, $"{frames} frames of reference"));
        int absent = Objects.Count(o => SeriesFlags.ReferencesAbsentSeries(study, o.Series));
        if (absent > 0)
            badges.Add(new InventoryBadge(FlagCodes.LinkReferencedSeriesAbsent, "referenced series absent"));
        Badges = badges;
    }

    public PatientNode Patient { get; }

    public DicomStudyGroup Study { get; }

    public string Key { get; }

    public IReadOnlyList<SeriesNode> ImageSeries { get; }

    /// <summary>Every other series of the study: RTSTRUCT, RTDOSE, RTPLAN and any other non-image modality.</summary>
    public IReadOnlyList<RtObjectRow> Objects { get; }

    /// <summary>Study-level badges; the series rows carry their own.</summary>
    public IReadOnlyList<InventoryBadge> Badges { get; }

    public string Label => string.IsNullOrWhiteSpace(Study.StudyDescription) ? "(no study description)" : Study.StudyDescription.Trim();
}

public sealed class SeriesNode
{
    internal SeriesNode(StudyNode study, DicomSeriesGroup series, string key)
    {
        Study = study;
        Series = series;
        Key = key;
        HasGeometry = SeriesGeometryProbe.TryDescribe(series, out var geometry);
        Geometry = HasGeometry ? geometry : null;
        IsOblique = SeriesFlags.IsOblique(series, out var orientation);
        Orientation = orientation;

        var badges = new List<InventoryBadge>();
        if (SeriesFlags.IsNonUniformZ(series, out string nonUniform))
            badges.Add(new InventoryBadge(FlagCodes.GeomNonUniformZ, "non-uniform Z", nonUniform));
        if (SeriesFlags.HasMissingPositions(series, out string missing))
            badges.Add(new InventoryBadge(FlagCodes.GeomUnpositioned, "unpositioned slices", missing));
        if (SeriesFlags.AnyLinkGuessed(series))
            badges.Add(new InventoryBadge(FlagCodes.LinkFallback, "link guessed"));
        if (IsOblique)
            badges.Add(new InventoryBadge(FlagCodes.GeomOblique, "oblique"));
        if (SeriesFlags.HasMixedOrientation(series))
            badges.Add(new InventoryBadge(FlagCodes.GeomMixedOrientation, "mixed orientation"));
        if (SeriesFlags.HasModalityMinority(series))
            badges.Add(new InventoryBadge(FlagCodes.GeomModalityMinority, "modality minority"));
        if (SeriesFlags.HasMultipleStructureSets(series))
            badges.Add(new InventoryBadge(FlagCodes.RtMultipleStructs, $"{series.LinkedRtStructs.Count} structure sets"));
        if (LinkedStructs.Concat(LinkedDoses).Any(rt => SeriesFlags.HasFrameOfReferenceMismatch(series, rt)))
            badges.Add(new InventoryBadge(FlagCodes.LinkForMismatch, "frame of reference mismatch"));
        Badges = badges;
    }

    public StudyNode Study { get; }

    public PatientNode Patient => Study.Patient;

    public DicomSeriesGroup Series { get; }

    public string Key { get; }

    public bool HasGeometry { get; }

    public SeriesGeometry? Geometry { get; }

    public bool IsOblique { get; }

    /// <summary>The slice plane and tilt, when the series has one orientation.</summary>
    public SeriesOrientation? Orientation { get; }

    public IReadOnlyList<InventoryBadge> Badges { get; }

    public IReadOnlyList<DicomSeriesGroup> LinkedStructs => LinkedOrSingle(Series.LinkedRtStructs, Series.LinkedRtStruct);

    public IReadOnlyList<DicomSeriesGroup> LinkedDoses => LinkedOrSingle(Series.LinkedRtDoses, Series.LinkedRtDose);

    public string Label => InventoryText.Describe(Series);

    // The scanner fills the lists; the singular link survives for older models. Prefer the list.
    private static IReadOnlyList<DicomSeriesGroup> LinkedOrSingle(List<DicomSeriesGroup>? list, DicomSeriesGroup? single)
    {
        if (list is { Count: > 0 })
            return list;
        return single is not null ? new[] { single } : Array.Empty<DicomSeriesGroup>();
    }
}

/// <summary>One non-image series of a study, and the image series it is linked to.</summary>
public sealed class RtObjectRow
{
    internal RtObjectRow(DicomStudyGroup study, DicomSeriesGroup rtObject)
    {
        Series = rtObject;
        IsLinkable = SeriesFlags.IsLinkable(rtObject);
        IsUnlinked = SeriesFlags.IsUnlinked(study, rtObject);
        IsGuess = SeriesFlags.IsLinkGuess(rtObject);
        LinkedTo = IsLinkable ? SeriesFlags.LinkedImageSeries(study, rtObject).ToList() : new List<DicomSeriesGroup>();
    }

    public DicomSeriesGroup Series { get; }

    /// <summary>RTSTRUCT or RTDOSE: what the scanner links. A plan or anything else is listed only.</summary>
    public bool IsLinkable { get; }

    public bool IsUnlinked { get; }

    public bool IsGuess { get; }

    public IReadOnlyList<DicomSeriesGroup> LinkedTo { get; }
}

/// <summary>How the inspectors write values, in the GUI inspector's words and formats.</summary>
public static class InventoryText
{
    public const string Missing = "—";

    public static string OrMissing(string? value) => string.IsNullOrEmpty(value) ? Missing : value;

    /// <summary>A DICOM DA value as "2024-03-05"; any other text trimmed as read.</summary>
    public static string Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Missing;
        string trimmed = value.Trim();
        return DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : trimmed;
    }

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Mm(double? value) => value.HasValue ? Number(value.Value) + " mm" : Missing;

    public static string Describe(DicomSeriesGroup? series) =>
        series is null ? Missing
        : string.IsNullOrWhiteSpace(series.SeriesDescription) ? "(no description)" : series.SeriesDescription.Trim();

    /// <summary>How the scanner matched an RT object, in the words the help uses.</summary>
    public static string Rule(RtLinkMatchRule rule) => rule switch
    {
        RtLinkMatchRule.ReferencedSeriesUid => "referenced series UID",
        RtLinkMatchRule.FrameOfReferenceUid => "frame of reference UID",
        RtLinkMatchRule.LargestSeriesFallback => "largest-series fallback (a guess)",
        _ => "an unrecorded rule",
    };

    /// <summary>"none", "1 linked, by referenced series UID", or "2 linked: 1 by ..., 1 by ...".</summary>
    public static string Links(IReadOnlyList<DicomSeriesGroup> linked)
    {
        if (linked.Count == 0)
            return "none";
        var byRule = linked.GroupBy(l => l.LinkMatchRule).OrderBy(g => g.Key == RtLinkMatchRule.None ? int.MaxValue : (int)g.Key).ToList();
        if (byRule.Count == 1)
            return string.Format(CultureInfo.InvariantCulture, "{0} linked, by {1}", linked.Count, Rule(byRule[0].Key));
        return string.Format(CultureInfo.InvariantCulture, "{0} linked: {1}", linked.Count,
            string.Join(", ", byRule.Select(g => string.Format(CultureInfo.InvariantCulture, "{0} by {1}", g.Count(), Rule(g.Key)))));
    }
}
