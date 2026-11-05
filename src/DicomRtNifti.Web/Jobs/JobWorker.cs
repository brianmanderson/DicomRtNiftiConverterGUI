namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// The worker of one lane, a hosted service: it takes job ids from the lane in order and runs
/// each to its end, <see cref="JobQueue.WorkersFor"/> at a time (one, by default). This is where
/// the work happens; a page only queues it.
///
/// A run takes the job's project lock before the job is marked running (a job that cannot have
/// its project fails without starting), hands the handler its log, progress callbacks and a
/// token, and records how it ended: the handler's outcome, <see cref="JobState.Cancelled"/> when
/// Cancel stopped it, <see cref="JobState.Interrupted"/> when the server is stopping, or
/// <see cref="JobState.Failed"/> on any exception. The lock is released and the log's last line
/// written before the job's final state is, so a running job always holds its project and a
/// finished one never does.
/// </summary>
public sealed class JobWorker : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly ProjectLocks _locks;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(JobLane lane, JobQueue queue, ProjectLocks locks, ILogger<JobWorker> logger)
    {
        Lane = lane;
        _queue = queue;
        _locks = locks;
        _logger = logger;
    }

    public JobLane Lane { get; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, _queue.WorkersFor(Lane))).Select(_ => LoopAsync(stoppingToken)));

    private async Task LoopAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (string jobId in _queue.Reader(Lane).ReadAllAsync(stopping))
            {
                if (stopping.IsCancellationRequested)
                    return; // the job stays queued on disk, and the next start queues it again
                await RunAsync(jobId, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(string jobId, CancellationToken stopping)
    {
        // The project first, so a job is only ever running while it holds its project: one that
        // cannot have it fails without starting.
        var queued = _queue.Find(jobId);
        if (queued is null || queued.State != JobState.Queued)
            return;
        ProjectLocks.Handle? held = null;
        if (queued.Project is not null)
        {
            try
            {
                held = _locks.Acquire(queued.Project, jobId);
            }
            catch (Exception ex) when (ex is ProjectLockedException or JobRefusedException)
            {
                _queue.FailBeforeStart(jobId, ex.Message);
                return;
            }
        }

        var run = _queue.TryStart(jobId, stopping);
        if (run is null)
        {
            held?.Dispose(); // cancelled while it waited for the project
            return;
        }

        var job = run.Job;
        var token = run.Cancellation.Token;
        JobState state;
        string? reason;
        try
        {
            run.Log.Report($"Job {job.Id}: {job.Title} (attempt {job.Attempt}).");

            var context = new JobContext(
                job,
                run.Log,
                progress => _queue.UpdateProgress(run, progress),
                json => _queue.SaveResult(run, json),
                token);
            // Off the worker's loop, so a handler that blocks before its first await holds a pool thread only.
            var outcome = await Task.Run(() => run.Handler.RunAsync(context), CancellationToken.None);

            (state, reason) = outcome.State is JobState.Succeeded or JobState.SucceededWithFailures or JobState.Failed
                ? (outcome.State, outcome.Message)
                : (JobState.Failed, $"The handler ended the job as {outcome.State}, which a run cannot end as.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            (state, reason) = run.CancelRequestedBy is { } user
                ? (JobState.Cancelled, $"Cancelled by {user}.")
                : (JobState.Interrupted, "The server stopped while this job was running. Retry runs it again.");
        }
        catch (JobRefusedException ex)
        {
            (state, reason) = (JobState.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            // The message names patients and paths: it goes to the job's own log and record only.
            _logger.LogError("Job {JobId} failed with {ExceptionType}; the job log has the details.", job.Id, ex.GetType().Name);
            run.Log.Report(ex.ToString());
            (state, reason) = (JobState.Failed, ex.Message);
        }
        finally
        {
            held?.Dispose();
        }

        run.Log.Report(state switch
        {
            JobState.Cancelled => "Job cancelled.",
            JobState.Interrupted => "Job interrupted: the server is stopping.",
            JobState.Failed => "Job failed: " + reason,
            _ => reason is null ? $"Job {state.DisplayName().ToLowerInvariant()}." : $"Job {state.DisplayName().ToLowerInvariant()}: {reason}",
        });
        await run.Log.CompleteAsync();
        _queue.Finish(run, state, reason);
    }
}

/// <summary>
/// Starts the job service before the workers, and stops it after them: hosted services start in
/// registration order and stop in reverse, and <see cref="JobServices"/> registers this first.
/// </summary>
internal sealed class JobRecovery : IHostedService
{
    private readonly JobQueue _queue;

    public JobRecovery(JobQueue queue)
    {
        _queue = queue;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _queue.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Stop();
        return Task.CompletedTask;
    }
}
