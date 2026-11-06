using System.Threading.Channels;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// A running job's log. Core reports its strings from whatever thread it is on (the rasterizer
/// from per-ROI workers), so <see cref="Report"/> only appends to a channel; one writer drains it
/// into <c>log.txt</c> in batches (plan section 3.2) and announces each batch on
/// <see cref="JobEvents"/>. The last lines are also kept in memory for the Jobs page's tail.
///
/// The log is PHI: Core's lines name patients and series. It goes to the job's own file only,
/// never to <c>ILogger</c>, and is redacted for paths where it is shown.
/// </summary>
internal sealed class JobLog : IProgress<string>
{
    /// <summary>Lines kept in memory for the tail.</summary>
    public const int TailCapacity = 500;

    private readonly string _jobId;
    private readonly JobStore.JobLogFile? _file;
    private readonly JobEvents _events;
    private readonly ILogger _logger;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> _tail = new();
    private readonly Task _writer;

    public JobLog(string jobId, JobStore.JobLogFile? file, JobEvents events, ILogger logger)
    {
        _jobId = jobId;
        _file = file;
        _events = events;
        _logger = logger;
        _writer = Task.Run(DrainAsync);
    }

    public void Report(string? value)
    {
        if (value is null)
            return;
        lock (_tail)
        {
            _tail.Enqueue(value);
            while (_tail.Count > TailCapacity)
                _tail.Dequeue();
        }

        _lines.Writer.TryWrite(value);
    }

    /// <summary>The last <paramref name="maxLines"/> lines reported, oldest first.</summary>
    public IReadOnlyList<string> Tail(int maxLines)
    {
        lock (_tail)
            return _tail.Skip(Math.Max(0, _tail.Count - maxLines)).ToList();
    }

    /// <summary>Stops taking lines, writes what is left, and closes the file.</summary>
    public async Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        await _writer;
        _file?.Dispose();
    }

    private async Task DrainAsync()
    {
        var batch = new List<string>();
        bool failed = false;
        while (await _lines.Reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < 1000 && _lines.Reader.TryRead(out string? line))
                batch.Add(line);

            if (_file is not null && !failed)
            {
                try
                {
                    _file.Append(batch);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the job running; the tail in memory still shows the lines.
                    failed = true;
                    _logger.LogError("Job {JobId}: its log file cannot be written ({ExceptionType}); later lines are kept in memory only.", _jobId, ex.GetType().Name);
                }
            }

            _events.Publish(_jobId, JobChangeKind.Log);
        }
    }
}
