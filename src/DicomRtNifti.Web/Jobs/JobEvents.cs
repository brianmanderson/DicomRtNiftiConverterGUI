namespace DicomRtNifti.Web.Jobs;

public enum JobChangeKind
{
    Queued,
    Started,
    Progress,
    Log,
    Finished,
}

/// <summary>Something happened to a job; read the job itself from <see cref="JobQueue.Find"/>.</summary>
public readonly record struct JobChange(string JobId, JobChangeKind Kind);

/// <summary>
/// Tells pages that a job changed. Events are raised on the thread that made the change, often a
/// worker's or one of Core's, and synchronously, so a handler must be cheap and must not block:
/// a component marks itself dirty and lets a <see cref="JobEventThrottle"/> re-render it through
/// <c>InvokeAsync</c>. A handler that throws is dropped from that one event, never from the job.
/// </summary>
public sealed class JobEvents
{
    private readonly ILogger<JobEvents> _logger;

    public JobEvents(ILogger<JobEvents> logger)
    {
        _logger = logger;
    }

    public event Action<JobChange>? Changed;

    internal void Publish(string jobId, JobChangeKind kind)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        var change = new JobChange(jobId, kind);
        foreach (Action<JobChange> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("A job-event subscriber threw {ExceptionType}; job {JobId} is unaffected.", ex.GetType().Name, jobId);
            }
        }
    }
}

/// <summary>
/// Calls back at most four times a second however fast jobs change (plan section 3.2), and once
/// more after the last change of a burst, so a page re-renders a busy job's progress smoothly
/// without a render per log line. Dispose to unsubscribe.
/// </summary>
public sealed class JobEventThrottle : IDisposable
{
    /// <summary>Four updates a second.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly JobEvents _events;
    private readonly Func<Task> _callback;
    private readonly Func<JobChange, bool>? _filter;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private bool _pending;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <param name="callback">Typically <c>() =&gt; InvokeAsync(StateHasChanged)</c>.</param>
    /// <param name="filter">Which changes count; null for all.</param>
    public JobEventThrottle(JobEvents events, Func<Task> callback, Func<JobChange, bool>? filter = null)
    {
        _events = events;
        _callback = callback;
        _filter = filter;
        _events.Changed += OnChanged;
    }

    public void Dispose()
    {
        _events.Changed -= OnChanged;
        _disposed.Cancel();
        _disposed.Dispose();
    }

    private void OnChanged(JobChange change)
    {
        if (_filter is not null && !_filter(change))
            return;

        TimeSpan delay;
        lock (_gate)
        {
            if (_pending || _disposed.IsCancellationRequested)
                return;
            _pending = true;
            delay = _last + Interval - DateTimeOffset.UtcNow;
        }

        _ = FireAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
    }

    private async Task FireAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _disposed.Token);
            lock (_gate)
            {
                _pending = false;
                _last = DateTimeOffset.UtcNow;
            }

            if (!_disposed.IsCancellationRequested)
                await _callback();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The component went away between the change and the render.
        }
    }
}
