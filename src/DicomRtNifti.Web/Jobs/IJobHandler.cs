using System.Text.Json;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// One kind of job: how to check and describe a request before it is queued, and how to run it
/// on a worker. Handlers are singletons registered as <see cref="IJobHandler"/>; a feature adds a
/// kind from its own <c>ConfigureServices</c>, so a page brief never edits this folder. The
/// handler holds no state between runs: everything a run needs is in its parameters, which are
/// what the job file keeps, so a queued or interrupted job runs again after a restart.
///
/// <see cref="Describe"/> runs on the caller's thread (a page's circuit), so it only resolves and
/// validates; all the work is in <see cref="RunAsync"/>, which runs on the lane's worker.
/// </summary>
public interface IJobHandler
{
    /// <summary>Lower-case, hyphenated, unique among the handlers: <c>scan</c>, <c>cohort-convert</c>.</summary>
    string Kind { get; }

    JobLane Lane { get; }

    /// <summary>Validates <paramref name="parameters"/> and describes the job.</summary>
    /// <exception cref="JobRefusedException">The request cannot run; the message says why.</exception>
    JobDescription Describe(JsonElement parameters);

    /// <summary>
    /// Runs the job. Throwing <see cref="OperationCanceledException"/> once
    /// <see cref="JobContext.CancellationToken"/> is cancelled ends it as cancelled (or
    /// interrupted, when the server is stopping); any other exception ends it as failed.
    /// </summary>
    Task<JobOutcome> RunAsync(JobContext context);
}

/// <param name="Title">What the Jobs page calls the job, paths as root tokens.</param>
/// <param name="Cli">The equivalent command line, paths as root tokens.</param>
/// <param name="Project">The output project the job writes, which it locks while it runs.</param>
public sealed record JobDescription(string Title, string Cli, string? Project = null);

/// <summary>How a run ended when it did not throw.</summary>
public sealed record JobOutcome(JobState State, string? Message = null)
{
    public static JobOutcome Succeeded(string? message = null) => new(JobState.Succeeded, message);

    public static JobOutcome WithFailures(string message) => new(JobState.SucceededWithFailures, message);

    public static JobOutcome Failed(string message) => new(JobState.Failed, message);
}

/// <summary>A request a handler will not queue. The message is shown to the user, redacted.</summary>
public sealed class JobRefusedException : Exception
{
    public JobRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// What a running job is handed: its parameters, its log, its progress callbacks, the token to
/// hand to Core, and the place for its result document. Everything here is safe to call from any
/// thread: Core reports from per-ROI workers.
/// </summary>
public sealed class JobContext
{
    private readonly Action<JobProgress> _report;
    private readonly Action<string> _setResult;
    private int _scanHighWater;

    internal JobContext(
        JobRecord job,
        IProgress<string> log,
        Action<JobProgress> report,
        Action<string> setResult,
        CancellationToken cancellationToken)
    {
        JobId = job.Id;
        Parameters = job.Parameters;
        Log = log;
        _report = report;
        _setResult = setResult;
        CancellationToken = cancellationToken;
        CohortProgress = new SynchronousProgress<CohortProgress>(OnCohortProgress);
        ScanProgress = new SynchronousProgress<ScanProgress>(OnScanProgress);
    }

    public string JobId { get; }

    public JsonElement Parameters { get; }

    /// <summary>The job log (<c>log.txt</c>): Core's own progress strings, unchanged.</summary>
    public IProgress<string> Log { get; }

    /// <summary>Hand this to Core; it is cancelled by Cancel and by the server stopping.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// For <c>ExecuteAsync</c> and <c>ComputeManifestAsync</c>: series i of n with running counts.
    /// Synchronous, not a <see cref="Progress{T}"/>, so the events keep Core's order.
    /// </summary>
    public IProgress<CohortProgress> CohortProgress { get; }

    /// <summary>For <c>ScanFolderAsync</c>: files read of files found, taken as a high-water mark.</summary>
    public IProgress<ScanProgress> ScanProgress { get; }

    /// <summary>The parameters as <typeparamref name="T"/>.</summary>
    public T ReadParameters<T>() => ReadParameters<T>(Parameters);

    /// <summary>The parameters as <typeparamref name="T"/>, or a refusal when they do not fit.</summary>
    public static T ReadParameters<T>(JsonElement parameters)
    {
        try
        {
            return parameters.Deserialize<T>(JobJson.Options)
                ?? throw new JobRefusedException("The job has no parameters.");
        }
        catch (JsonException)
        {
            throw new JobRefusedException("The job's parameters are malformed.");
        }
    }

    public void Report(JobProgress progress) => _report(progress);

    /// <summary>Stores the job's result document (<c>result.json</c>).</summary>
    public void SetResult(string json) => _setResult(json);

    private void OnCohortProgress(CohortProgress e)
    {
        int done = e.Phase == CohortProgressPhase.SeriesStarted ? e.SeriesIndex - 1 : e.SeriesIndex;
        Report(new JobProgress("series", done, e.SeriesCount, true, e.SucceededCount, e.FailedCount, e.SeriesLabel));
    }

    private void OnScanProgress(ScanProgress e)
    {
        // Reads finish on many threads, so an event can arrive after a later one: keep the highest.
        int seen;
        do
        {
            seen = Volatile.Read(ref _scanHighWater);
            if (e.FilesProcessed < seen && !e.WalkFinished)
                return;
        }
        while (Interlocked.CompareExchange(ref _scanHighWater, Math.Max(seen, e.FilesProcessed), seen) != seen);

        Report(new JobProgress("files", Math.Max(seen, e.FilesProcessed), e.FilesFound, e.WalkFinished));
    }
}

/// <summary>An <see cref="IProgress{T}"/> that calls back on the reporting thread, in order.</summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler)
    {
        _handler = handler;
    }

    public void Report(T value) => _handler(value);
}
