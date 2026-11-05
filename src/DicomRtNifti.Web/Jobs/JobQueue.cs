using System.Text.Json;
using System.Threading.Channels;
using DicomRtNifti.Web.Audit;
using DicomRtNifti.Web.Hosting;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Jobs;

/// <summary>What <see cref="JobQueue.Submit"/> did: queued <see cref="Job"/>, or refused and why.</summary>
public sealed record JobSubmission(JobRecord? Job, string? Refusal)
{
    public bool Accepted => Job is not null;
}

/// <summary>What <see cref="JobQueue.Cancel"/> or <see cref="JobQueue.Retry"/> did.</summary>
public sealed record JobActionResult(bool Done, string Message);

/// <summary>A lane at a glance: its workers, how many jobs wait and run, and how many may wait.</summary>
public sealed record LaneStatus(JobLane Lane, int Workers, int Capacity, int Queued, int Running);

/// <summary>
/// The job service (plan section 3.2): every job the server knows, the two lanes that feed the
/// workers, and the transitions between states. Pages call <see cref="Submit"/>,
/// <see cref="Cancel"/> and <see cref="Retry"/>, which return at once; the work itself runs on a
/// <see cref="JobWorker"/>, never on a circuit.
///
/// A lane is a channel of job ids with a capacity per lane (<c>Web:Lanes:QueueCapacity</c>,
/// counted over the jobs waiting in it), so a full lane refuses with a message rather than
/// blocking a page. Each transition writes the job's file (<see cref="JobStore"/>), an audit
/// record (<see cref="AuditLog"/>) and an event (<see cref="JobEvents"/>). On start
/// (<see cref="Start"/>) a job recorded as running belongs to a server that died, so it becomes
/// <see cref="JobState.Interrupted"/> with a reason and its project lock is released; queued jobs
/// go back into their lanes.
///
/// The application log gets job ids, kinds, lanes, states, user ids and exception type names:
/// never a title, a parameter, a log line or an exception message, which carry patient
/// identifiers and paths (plan section 6).
/// </summary>
public sealed class JobQueue
{
    /// <summary>How often a running job's progress is written to its file, at most.</summary>
    public static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(1);

    private const string ServerUser = "server";

    private readonly object _gate = new();
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningJob> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<JobLane, Channel<string>> _lanes = new();
    private readonly Dictionary<string, IJobHandler> _handlers = new(StringComparer.Ordinal);
    private readonly JobStore _store;
    private readonly ProjectLocks _locks;
    private readonly AuditLog _audit;
    private readonly JobEvents _events;
    private readonly PathRedactor _redactor;
    private readonly WebLanes _laneOptions;
    private readonly ILogger<JobQueue> _logger;
    private string? _unavailable = "The job service has not started yet.";
    private bool _started;

    public JobQueue(
        JobStore store,
        ProjectLocks locks,
        AuditLog audit,
        JobEvents events,
        PathRedactor redactor,
        IEnumerable<IJobHandler> handlers,
        IOptions<WebOptions> options,
        ILogger<JobQueue> logger)
    {
        _store = store;
        _locks = locks;
        _audit = audit;
        _events = events;
        _redactor = redactor;
        _laneOptions = options.Value.Lanes ?? new WebLanes();
        _logger = logger;

        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Kind, handler))
                throw new InvalidOperationException($"Two job handlers declare the kind '{handler.Kind}'.");
        }

        foreach (JobLane lane in Enum.GetValues<JobLane>())
            _lanes[lane] = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = false });
    }

    /// <summary>Whether jobs can be submitted; when not, <see cref="UnavailableReason"/> says why.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
                return _unavailable is null;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            lock (_gate)
                return _unavailable;
        }
    }

    /// <summary>The job kinds this server runs.</summary>
    public IReadOnlyCollection<string> Kinds => _handlers.Keys;

    /// <summary>How many jobs may wait in a lane.</summary>
    public int Capacity => _laneOptions.QueueCapacity;

    /// <summary>Workers per lane: <c>Web:Lanes:MaxHeavyWorkers</c> and <c>MaxLightWorkers</c>, one each by default.</summary>
    public int WorkersFor(JobLane lane) => lane == JobLane.Heavy ? _laneOptions.MaxHeavyWorkers : _laneOptions.MaxLightWorkers;

    // -----------------------------------------------------------------------------------
    //  Start and stop (JobRecovery calls these as the host starts and stops)
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Takes the state folder and recovers what the last run of the server left: running jobs
    /// become interrupted (their project locks are released), queued jobs are queued again.
    /// </summary>
    public void Start()
    {
        int interrupted = 0, requeued = 0, total;
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;

            string? refusal = _store.Open();
            if (refusal is not null)
            {
                _unavailable = refusal;
                _logger.LogWarning("Jobs are off: {Reason}", refusal);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var loaded in _store.LoadAll())
            {
                var job = loaded;
                if (job.State == JobState.Running)
                {
                    job = job with
                    {
                        State = JobState.Interrupted,
                        FinishedUtc = now,
                        Reason = "The server stopped while this job was running (found at the next start). Retry runs it again.",
                    };
                    Persist(job);
                    if (job.Project is not null)
                        _locks.ReleaseStale(job.Project, job.Id);
                    Audit(job, AuditActions.JobInterrupted, ServerUser, job.Reason);
                    interrupted++;
                }
                else if (job.State == JobState.Queued)
                {
                    if (_handlers.ContainsKey(job.Kind))
                    {
                        _lanes[job.Lane].Writer.TryWrite(job.Id);
                        requeued++;
                    }
                    else
                    {
                        job = job with { State = JobState.Failed, FinishedUtc = now, Reason = $"This server has no job kind '{job.Kind}'." };
                        Persist(job);
                    }
                }

                _jobs[job.Id] = job;
            }

            total = _jobs.Count;
            _unavailable = null;
        }

        _logger.LogInformation(
            "Job service started: {Total} job(s) on record, {Interrupted} interrupted by the last stop, {Requeued} queued again.",
            total, interrupted, requeued);
    }

    /// <summary>Releases the state folder. Workers have stopped by now (they stop first).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _unavailable = "The job service has stopped.";
            _store.Dispose();
        }
    }

    // -----------------------------------------------------------------------------------
    //  What pages call
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Queues a job of <paramref name="kind"/> for <paramref name="user"/>, or refuses it: the
    /// service is off, the kind is unknown, the handler refuses the parameters, the lane is full,
    /// or the audit record cannot be written. Returns at once.
    /// </summary>
    /// <param name="parameters">The handler's parameter object (or a <see cref="JsonElement"/>).</param>
    public JobSubmission Submit(string kind, object? parameters, string user)
    {
        if (UnavailableReason is { } off)
            return new JobSubmission(null, off);
        if (!_handlers.TryGetValue(kind, out var handler))
            return new JobSubmission(null, $"There is no job kind '{kind}'.");

        var element = parameters is JsonElement given ? given.Clone() : JobJson.ToElement(parameters);
        JobDescription description;
        try
        {
            description = handler.Describe(element);
            if (description.Project is not null)
                _locks.ResolveProject(description.Project);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A handler's own refusal says why; anything else is a bug in the check, reported
            // by type so the page gets an answer rather than a broken circuit.
            string refusal = ex is JobRefusedException
                ? _redactor.Redact(ex.Message)
                : $"The request could not be checked ({ex.GetType().Name}); nothing was queued.";
            if (ex is not JobRefusedException)
                _logger.LogError("Checking a {Kind} job threw {ExceptionType}.", kind, ex.GetType().Name);
            TryAudit(new AuditEntry { User = user, Action = AuditActions.JobRefused, Kind = kind, Outcome = "refused", Detail = refusal });
            return new JobSubmission(null, refusal);
        }

        JobRecord job;
        lock (_gate)
        {
            if (_unavailable is not null)
                return new JobSubmission(null, _unavailable);
            if (QueuedIn(handler.Lane) >= Capacity)
                return new JobSubmission(null, LaneFull(handler.Lane));

            job = new JobRecord
            {
                Id = JobStore.NewId(),
                Kind = kind,
                Lane = handler.Lane,
                Title = description.Title,
                Cli = description.Cli,
                Project = description.Project,
                Parameters = element,
                User = user,
                State = JobState.Queued,
                CreatedUtc = DateTimeOffset.UtcNow,
            };

            // The record of who asked comes before the job: no audit, no job.
            try
            {
                Audit(job, AuditActions.JobSubmitted, user, null, throwOnFailure: true);
                _store.Save(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Job {JobId} was refused: its records cannot be written ({ExceptionType}).", job.Id, ex.GetType().Name);
                return new JobSubmission(null, "The job's records cannot be written to the state folder; nothing was queued.");
            }

            _jobs[job.Id] = job;
            _lanes[job.Lane].Writer.TryWrite(job.Id);
        }

        _logger.LogInformation("Job {JobId} queued: {Kind} on the {Lane} lane, by {User}.", job.Id, job.Kind, job.Lane, user);
        _events.Publish(job.Id, JobChangeKind.Queued);
        return new JobSubmission(job, null);
    }

    /// <summary>
    /// Cancels a job: a queued one at once, a running one by cancelling the token its handler
    /// handed to Core (it ends as cancelled when Core next checks it).
    /// </summary>
    public JobActionResult Cancel(string jobId, string user)
    {
        CancellationTokenSource? toCancel = null;
        JobRecord? cancelled = null;
        string message;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return new JobActionResult(false, "There is no such job.");

            if (job.State == JobState.Queued)
            {
                cancelled = job with
                {
                    State = JobState.Cancelled,
                    FinishedUtc = DateTimeOffset.UtcNow,
                    Reason = $"Cancelled by {user} before it started.",
                };
                Persist(cancelled);
                _jobs[jobId] = cancelled;
                Audit(cancelled, AuditActions.JobCancelRequested, user, cancelled.Reason);
                message = "Cancelled before it started.";
            }
            else if (job.State == JobState.Running && _running.TryGetValue(jobId, out var run))
            {
                if (run.CancelRequestedBy is not null)
                    return new JobActionResult(false, "The job is already being cancelled.");
                run.CancelRequestedBy = user;
                toCancel = run.Cancellation;
                Audit(job, AuditActions.JobCancelRequested, user, "Cancel requested while running.");
                message = "Cancelling: the job stops at the next series or file.";
            }
            else
            {
                return new JobActionResult(false, "The job has already finished.");
            }
        }

        if (cancelled is not null)
        {
            _logger.LogInformation("Job {JobId} cancelled before it started, by {User}.", jobId, user);
            _events.Publish(jobId, JobChangeKind.Finished);
        }
        else
        {
            _logger.LogInformation("Job {JobId}: cancel requested by {User}.", jobId, user);
            // Outside the lock: cancellation runs Core's registered callbacks on this thread.
            toCancel!.Cancel();
        }

        return new JobActionResult(true, message);
    }

    /// <summary>
    /// Queues an interrupted, failed or cancelled job again, as the same job with its attempt
    /// counted. A cohort conversion is safe to re-run: outputs are overwritten in place and the
    /// manifest merges by its identifier columns.
    /// </summary>
    public JobActionResult Retry(string jobId, string user)
    {
        JobRecord? job = Find(jobId);
        if (job is null)
            return new JobActionResult(false, "There is no such job.");
        if (!job.State.CanRetry())
            return new JobActionResult(false, $"A job that is {job.State.DisplayName().ToLowerInvariant()} cannot be retried.");
        if (!_handlers.TryGetValue(job.Kind, out var handler))
            return new JobActionResult(false, $"This server has no job kind '{job.Kind}'.");

        // The roots or the configuration may have changed since the job first ran.
        try
        {
            handler.Describe(job.Parameters);
        }
        catch (JobRefusedException ex)
        {
            return new JobActionResult(false, _redactor.Redact(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Checking job {JobId} for a retry threw {ExceptionType}.", jobId, ex.GetType().Name);
            return new JobActionResult(false, $"The job could not be checked ({ex.GetType().Name}); it was not queued again.");
        }

        JobRecord retried;
        lock (_gate)
        {
            if (_unavailable is not null)
                return new JobActionResult(false, _unavailable);
            if (!_jobs.TryGetValue(jobId, out var current) || !current.State.CanRetry())
                return new JobActionResult(false, "The job changed meanwhile; look again.");
            if (QueuedIn(current.Lane) >= Capacity)
                return new JobActionResult(false, LaneFull(current.Lane));

            retried = current with
            {
                State = JobState.Queued,
                Attempt = current.Attempt + 1,
                Reason = null,
                StartedUtc = null,
                FinishedUtc = null,
                Progress = JobProgress.None,
            };
            try
            {
                Audit(retried, AuditActions.JobRetried, user, $"Attempt {retried.Attempt}.", throwOnFailure: true);
                _store.Save(retried);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Job {JobId}: the retry was refused, its records cannot be written ({ExceptionType}).", jobId, ex.GetType().Name);
                return new JobActionResult(false, "The job's records cannot be written to the state folder; it was not queued again.");
            }

            _jobs[jobId] = retried;
            _lanes[retried.Lane].Writer.TryWrite(jobId);
        }

        _logger.LogInformation("Job {JobId} queued again by {User} (attempt {Attempt}).", jobId, user, retried.Attempt);
        _events.Publish(jobId, JobChangeKind.Queued);
        return new JobActionResult(true, $"Queued again (attempt {retried.Attempt}).");
    }

    /// <summary>Every job, newest first.</summary>
    public IReadOnlyList<JobRecord> List()
    {
        lock (_gate)
            return _jobs.Values.OrderByDescending(j => j.CreatedUtc).ThenByDescending(j => j.Id, StringComparer.Ordinal).ToList();
    }

    public JobRecord? Find(string? jobId)
    {
        if (jobId is null)
            return null;
        lock (_gate)
            return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public LaneStatus Status(JobLane lane)
    {
        lock (_gate)
            return new LaneStatus(lane, WorkersFor(lane), Capacity, QueuedIn(lane), _jobs.Values.Count(j => j.Lane == lane && j.State == JobState.Running));
    }

    /// <summary>The last lines of a job's log, as written: render them through the path redactor.</summary>
    public IReadOnlyList<string> LogTail(string jobId, int maxLines)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(jobId, out var run))
                return run.Log.Tail(maxLines);
            if (!_jobs.ContainsKey(jobId))
                return Array.Empty<string>();
        }

        try
        {
            return _store.ReadLogTail(jobId, maxLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Job {JobId}: its log cannot be read ({ExceptionType}).", jobId, ex.GetType().Name);
            return Array.Empty<string>();
        }
    }

    /// <summary>The job's result document (<c>result.json</c>), or null.</summary>
    public string? ReadResult(string jobId)
    {
        if (Find(jobId) is null)
            return null;
        try
        {
            return _store.ReadResult(jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Job {JobId}: its result cannot be read ({ExceptionType}).", jobId, ex.GetType().Name);
            return null;
        }
    }

    // -----------------------------------------------------------------------------------
    //  What workers call
    // -----------------------------------------------------------------------------------

    internal ChannelReader<string> Reader(JobLane lane) => _lanes[lane].Reader;

    /// <summary>Marks a queued job running and opens its log; null when it is no longer queued (cancelled while waiting).</summary>
    internal RunningJob? TryStart(string jobId, CancellationToken stopping)
    {
        RunningJob run;
        lock (_gate)
        {
            if (_unavailable is not null || !_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Queued)
                return null;
            if (!_handlers.TryGetValue(job.Kind, out var handler))
                return null;

            var started = job with { State = JobState.Running, StartedUtc = DateTimeOffset.UtcNow, Progress = JobProgress.None };
            JobStore.JobLogFile? file = null;
            try
            {
                _store.Save(started);
                file = _store.OpenLog(jobId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Job {JobId}: its files cannot be written ({ExceptionType}); it runs with its log in memory only.", jobId, ex.GetType().Name);
            }

            run = new RunningJob(started, handler, new JobLog(jobId, file, _events, _logger), CancellationTokenSource.CreateLinkedTokenSource(stopping));
            _jobs[jobId] = started;
            _running[jobId] = run;
            Audit(started, AuditActions.JobStarted, ServerUser, $"Attempt {started.Attempt}.");
        }

        _logger.LogInformation("Job {JobId} started on the {Lane} lane (attempt {Attempt}).", jobId, run.Job.Lane, run.Job.Attempt);
        _events.Publish(jobId, JobChangeKind.Started);
        return run;
    }

    /// <summary>Ends a queued job as failed without starting it: its project is held elsewhere.</summary>
    internal void FailBeforeStart(string jobId, string reason)
    {
        JobRecord failed;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Queued)
                return;
            failed = job with { State = JobState.Failed, Reason = reason, FinishedUtc = DateTimeOffset.UtcNow };
            Persist(failed);
            _jobs[jobId] = failed;
            Audit(failed, AuditActions.JobFinished, ServerUser, reason);
            try
            {
                using var log = _store.OpenLog(jobId);
                log.Append(new[] { $"Job {jobId}: {job.Title} (attempt {job.Attempt}).", "Job failed: " + reason });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Job {JobId}: its log file cannot be written ({ExceptionType}).", jobId, ex.GetType().Name);
            }
        }

        _logger.LogInformation("Job {JobId} failed before it started: its project is in use.", jobId);
        _events.Publish(jobId, JobChangeKind.Finished);
    }

    internal void UpdateProgress(RunningJob run, JobProgress progress)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(run.Job.Id, out var job) || job.State != JobState.Running)
                return;
            job = job with { Progress = progress };
            _jobs[job.Id] = job;
            var now = DateTimeOffset.UtcNow;
            if (now - run.LastSaved >= ProgressSaveInterval || (progress.TotalKnown && progress.Done == progress.Total))
            {
                run.LastSaved = now;
                Persist(job);
            }
        }

        _events.Publish(run.Job.Id, JobChangeKind.Progress);
    }

    internal void SaveResult(RunningJob run, string json) => _store.SaveResult(run.Job.Id, json);

    /// <summary>Records how a run ended. The worker has closed the log and released the project by now.</summary>
    internal void Finish(RunningJob run, JobState state, string? reason)
    {
        JobRecord finished;
        lock (_gate)
        {
            var job = _jobs[run.Job.Id];
            finished = job with { State = state, Reason = reason, FinishedUtc = DateTimeOffset.UtcNow };
            Persist(finished);
            _jobs[finished.Id] = finished;
            _running.Remove(finished.Id);
            Audit(finished, state == JobState.Interrupted ? AuditActions.JobInterrupted : AuditActions.JobFinished, ServerUser, reason);
        }

        run.Cancellation.Dispose();
        _logger.LogInformation("Job {JobId} finished: {State} after {Seconds:F1} s.", finished.Id, finished.State, finished.Elapsed?.TotalSeconds ?? 0);
        _events.Publish(finished.Id, JobChangeKind.Finished);
    }

    // -----------------------------------------------------------------------------------

    private int QueuedIn(JobLane lane) => _jobs.Values.Count(j => j.Lane == lane && j.State == JobState.Queued);

    private string LaneFull(JobLane lane) =>
        $"The {lane.ToString().ToLowerInvariant()} lane's queue is full ({Capacity} jobs waiting); try again once one has started.";

    private void Persist(JobRecord job)
    {
        try
        {
            _store.Save(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Job {JobId}: its job file cannot be written ({ExceptionType}).", job.Id, ex.GetType().Name);
        }
    }

    private void Audit(JobRecord job, string action, string user, string? detail, bool throwOnFailure = false)
    {
        var entry = new AuditEntry
        {
            User = user,
            Action = action,
            JobId = job.Id,
            Kind = job.Kind,
            Project = job.Project,
            Target = job.Title,
            Cli = job.Cli,
            Outcome = job.State.ToString(),
            Detail = detail,
        };
        if (throwOnFailure)
            _audit.Write(entry);
        else
            TryAudit(entry);
    }

    private void TryAudit(AuditEntry entry)
    {
        try
        {
            _audit.Write(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("An audit record ({Action}, job {JobId}) cannot be written ({ExceptionType}).", entry.Action, entry.JobId, ex.GetType().Name);
        }
    }
}

/// <summary>A job a worker is running: its handler, its log, and the token its handler hands to Core.</summary>
internal sealed class RunningJob
{
    public RunningJob(JobRecord job, IJobHandler handler, JobLog log, CancellationTokenSource cancellation)
    {
        Job = job;
        Handler = handler;
        Log = log;
        Cancellation = cancellation;
    }

    /// <summary>The record as it started.</summary>
    public JobRecord Job { get; }

    public IJobHandler Handler { get; }

    public JobLog Log { get; }

    public CancellationTokenSource Cancellation { get; }

    /// <summary>Who pressed Cancel, once someone did: tells a cancellation from a server stop.</summary>
    public string? CancelRequestedBy { get; set; }

    public DateTimeOffset LastSaved { get; set; } = DateTimeOffset.MinValue;
}
