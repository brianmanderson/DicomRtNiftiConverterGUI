using System.Text.Json;

namespace DicomRtNifti.Web.Features.Plan;

/// <summary>
/// The plan document Core built (<c>dicomrtnifti.cohort-plan/1</c>), read for display: the fields
/// Plan review shows, as the document states them. Reading only: every value here is Core's, and a
/// field this page does not show is still in the document the job recorded.
/// </summary>
public sealed class PlanDocumentView
{
    public sealed record Series(
        string PatientId, string SeriesUid, string Modality, string Description, int Instances,
        string OutputDir, string? StructureSet, IReadOnlyList<string> Rois, int Doses, IReadOnlyList<string> Warnings);

    public sealed record Excluded(string PatientId, string SeriesUid, string Modality, string Description, string Reason);

    /// <summary>One folder of the output tree preview; <see cref="Series"/> marks a series' own folder.</summary>
    public sealed class Folder
    {
        public string Name { get; init; } = "";
        public List<Folder> Children { get; } = new();
        public Series? Series { get; set; }
    }

    public string Mode { get; private init; } = "";
    public string? OutputRoot { get; private init; }
    public string? ManifestCsv { get; private init; }
    public bool Anonymized { get; private init; }
    public string? AnonymizationKey { get; private init; }
    public string? OutputSpacing { get; private init; }
    public int SeriesCount { get; private init; }
    public IReadOnlyList<Series> Planned { get; private init; } = Array.Empty<Series>();
    public IReadOnlyList<Excluded> Skipped { get; private init; } = Array.Empty<Excluded>();
    public IReadOnlyList<Excluded> Unlinked { get; private init; } = Array.Empty<Excluded>();
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>The planned output folders as a tree, from each series' <c>output_dir</c>.</summary>
    public Folder Tree { get; private init; } = new();

    public static PlanDocumentView Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var planned = Items(root, "series").Select(s => new Series(
            Text(s, "patient_id"), Text(s, "series_uid"), Text(s, "modality"), Text(s, "series_description"),
            s.TryGetProperty("instance_count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0,
            Text(s, "output_dir"),
            s.TryGetProperty("structure_set", out var set) && set.ValueKind == JsonValueKind.Object
                ? $"{Text(set, "series_description")} ({Text(set, "match_rule")})"
                : null,
            Items(s, "rois").Select(Describe).ToList(),
            Items(s, "doses").Count(),
            Items(s, "warnings").Select(Describe).ToList())).ToList();

        return new PlanDocumentView
        {
            Mode = Text(root, "mode"),
            OutputRoot = NullableText(root, "output_root"),
            ManifestCsv = NullableText(root, "manifest_csv"),
            Anonymized = root.TryGetProperty("anonymized", out var anonymized) && anonymized.ValueKind == JsonValueKind.True,
            AnonymizationKey = NullableText(root, "anonymization_key"),
            OutputSpacing = root.TryGetProperty("output_spacing", out var spacing) && spacing.ValueKind == JsonValueKind.Array
                ? string.Join(" × ", spacing.EnumerateArray().Select(v => v.GetRawText())) + " mm"
                : null,
            SeriesCount = root.TryGetProperty("series_count", out var seriesCount) ? seriesCount.GetInt32() : planned.Count,
            Planned = planned,
            Skipped = Items(root, "skipped").Select(ExcludedOf).ToList(),
            Unlinked = Items(root, "unlinked").Select(ExcludedOf).ToList(),
            Warnings = Items(root, "warnings").Select(Describe).ToList(),
            Tree = TreeOf(planned),
        };
    }

    private static Folder TreeOf(IEnumerable<Series> planned)
    {
        var root = new Folder();
        foreach (var series in planned)
        {
            var folder = root;
            foreach (string segment in series.OutputDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = folder.Children.FirstOrDefault(c => c.Name == segment);
                if (child is null)
                {
                    child = new Folder { Name = segment };
                    folder.Children.Add(child);
                }

                folder = child;
            }

            folder.Series = series;
        }

        return root;
    }

    private static Excluded ExcludedOf(JsonElement e) => new(
        Text(e, "patient_id"), Text(e, "series_instance_uid"), Text(e, "modality"), Text(e, "series_description"), Text(e, "reason"));

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement element, string name) => NullableText(element, name) ?? "";

    private static string? NullableText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        } : null;

    /// <summary>A string as it is; anything else as its JSON.</summary>
    private static string Describe(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
}
