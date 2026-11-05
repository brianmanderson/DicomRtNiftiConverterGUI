using System.Text.Json;
using System.Text.Json.Serialization;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// The two lanes (plan section 3.2). <see cref="Heavy"/> takes anything that decodes pixels,
/// rasterizes or writes NIfTI or DICOM (a conversion, a survey); <see cref="Light"/> takes
/// header-only work (a scan). One worker each by default: a conversion already saturates the
/// machine, so a second one beside it would only make both slower.
/// </summary>
public enum JobLane
{
    Light,
    Heavy,
}

/// <summary>Where a job is (plan section 4.8).</summary>
public enum JobState
{
    Queued,
    Running,
    Succeeded,

    /// <summary>Finished, but some of its series failed (<c>CohortExportResult.FailedCount</c>).</summary>
    SucceededWithFailures,

    Failed,
    Cancelled,

    /// <summary>The server stopped while the job ran; <see cref="JobQueue.Retry"/> runs it again.</summary>
    Interrupted,
}

public static class JobStateExtensions
{
    /// <summary>Whether the job has ended and will not change again unless it is retried.</summary>
    public static bool IsFinished(this JobState state) => state is not (JobState.Queued or JobState.Running);

    /// <summary>Whether <see cref="JobQueue.Retry"/> accepts a job in this state.</summary>
    public static bool CanRetry(this JobState state) =>
        state is JobState.Interrupted or JobState.Failed or JobState.Cancelled;

    public static string DisplayName(this JobState state) => state switch
    {
        JobState.SucceededWithFailures => "Succeeded with failures",
        _ => state.ToString(),
    };
}

/// <summary>
/// How far a running job has come, in whatever it counts: series for a conversion or a survey,
/// files for a scan. <see cref="Current"/> is the label Core's log prints for the series in hand,
/// which names the patient, so it is shown to authorized users and never logged.
/// </summary>
public sealed record JobProgress(
    string Unit,
    int Done,
    int Total,
    bool TotalKnown,
    int Succeeded = 0,
    int Failed = 0,
    string? Current = null)
{
    public static readonly JobProgress None = new("", 0, 0, false);

    public bool HasCounts => Unit.Length > 0;
}

/// <summary>
/// One job as <c>StateRoot/jobs/&lt;id&gt;/job.json</c> holds it (D-38: a JSON file per job, no
/// database). A record never changes: every transition writes a new one, so a page can hold a
/// snapshot while the worker moves on.
/// </summary>
public sealed record JobRecord
{
    public const string CurrentSchema = "dicomrtnifti.web-job/1";

    public string Schema { get; init; } = CurrentSchema;

    /// <summary>32 lower-case hex digits; opaque, so it may appear in logs and in a page's state.</summary>
    public required string Id { get; init; }

    /// <summary>The handler that runs it (<see cref="IJobHandler.Kind"/>).</summary>
    public required string Kind { get; init; }

    public JobLane Lane { get; init; }

    /// <summary>What the Jobs page calls it; names root tokens, so it is PHI and never logged.</summary>
    public required string Title { get; init; }

    /// <summary>The command line that does the same thing (plan section 4.8), paths as root tokens.</summary>
    public required string Cli { get; init; }

    /// <summary>The output project the job writes, which it locks while it runs; null when it writes none.</summary>
    public string? Project { get; init; }

    /// <summary>What the handler needs to run the job again after a restart.</summary>
    public JsonElement Parameters { get; init; }

    /// <summary>Who submitted it.</summary>
    public required string User { get; init; }

    public JobState State { get; init; }

    /// <summary>Why it ended as it did: the failure, the interruption, the refusal; null on plain success.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>1 for the first run; each retry adds one.</summary>
    public int Attempt { get; init; } = 1;

    public JobProgress Progress { get; init; } = JobProgress.None;

    [JsonIgnore]
    public TimeSpan? Elapsed => StartedUtc is { } started ? (FinishedUtc ?? DateTimeOffset.UtcNow) - started : null;
}

/// <summary>The one JSON shape of the job files: snake_case, enums as names, indented.</summary>
internal static class JobJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The same, on one line, for the audit log's JSON lines.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), Options);
}
