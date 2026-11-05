using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// The jobs on disk (D-38): one folder per job under <c>StateRoot/jobs/</c>, holding
/// <c>job.json</c> (the <see cref="JobRecord"/>, rewritten whole through Core's
/// <see cref="AtomicFileWriter"/>, so a kill mid-write leaves the previous record), <c>log.txt</c>
/// (append-only) and <c>result.json</c> (the Core document the job produced). No database.
///
/// One server owns a state folder at a time: <see cref="Open"/> takes <c>jobs/owner.lock</c> for
/// the life of the process, because restart recovery marks every running job interrupted, which is
/// only true when no other live process is running them. Every path here comes from
/// <see cref="DataRootResolver.ResolveServerPath"/>; this is one of the few types allowed to touch
/// the filesystem (the architecture test lists them).
/// </summary>
public sealed partial class JobStore : IDisposable
{
    public const string JobsFolder = "jobs";
    public const string JobFile = "job.json";
    public const string LogFile = "log.txt";
    public const string ResultFile = "result.json";
    private const string OwnerFile = "owner.lock";

    /// <summary>How much of the end of a log a tail reads at most.</summary>
    private const int TailBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DataRoots _roots;
    private readonly DataRootResolver _resolver;
    private readonly ILogger<JobStore> _logger;
    private FileStream? _owner;

    public JobStore(DataRoots roots, DataRootResolver resolver, ILogger<JobStore> logger)
    {
        _roots = roots;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>Whether <c>Web:StateRoot</c> is set at all.</summary>
    public bool IsConfigured => _roots.Folder(ServerFolder.State) is not null;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex JobIdPattern();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsJobId(string? id) => id is not null && JobIdPattern().IsMatch(id);

    /// <summary>
    /// Takes the state folder for this process. Returns null when it did, or why the jobs are
    /// unavailable; the reason names configuration keys and process ids, never a path.
    /// </summary>
    public string? Open()
    {
        if (!IsConfigured)
            return "Jobs need a state folder: set Web:StateRoot in appsettings.json or the environment.";
        if (_owner is not null)
            return null;

        var path = _resolver.ResolveServerPath(ServerFolder.State, JobsFolder + "/" + OwnerFile);
        try
        {
            Directory.CreateDirectory(_resolver.ResolveServerPath(ServerFolder.State, JobsFolder).FullPath);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    // Deleted when the handle closes: on Dispose, and on Windows also when the
                    // process dies. A file left by a killed process elsewhere is taken over below.
                    var owner = new FileStream(path.FullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
                    var stamp = Utf8.GetBytes(JsonSerializer.Serialize(ProcessStamp.Current(), JobJson.Compact));
                    owner.Write(stamp);
                    owner.Flush(true);
                    _owner = owner;
                    return null;
                }
                catch (IOException) when (File.Exists(path.FullPath))
                {
                    var holder = ProcessStamp.Read(path.FullPath);
                    if (holder is not null && holder.IsAnotherLiveProcess())
                        return $"Another server process (id {holder.ProcessId} on {holder.Machine}) owns this state folder; jobs are off in this one.";
                    File.Delete(path.FullPath);
                }
            }

            return "The state folder's owner lock could not be taken.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("The job state folder cannot be written ({ExceptionType}); jobs are off.", ex.GetType().Name);
            return $"The state folder cannot be written ({ex.GetType().Name}); jobs are off.";
        }
    }

    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
    }

    /// <summary>Every job on disk, oldest first. A folder whose job file cannot be read is skipped and logged by id.</summary>
    public IReadOnlyList<JobRecord> LoadAll()
    {
        var jobs = new List<JobRecord>();
        var folder = _resolver.ResolveServerPath(ServerFolder.State, JobsFolder);
        if (!Directory.Exists(folder.FullPath))
            return jobs;

        foreach (string directory in Directory.EnumerateDirectories(folder.FullPath))
        {
            string id = Path.GetFileName(directory);
            if (!IsJobId(id))
                continue;
            try
            {
                var file = JobPath(id, JobFile);
                if (!File.Exists(file.FullPath))
                    continue;
                var job = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(file.FullPath, Utf8), JobJson.Options);
                if (job is null || job.Id != id || job.Schema != JobRecord.CurrentSchema)
                    throw new JsonException("The job file does not describe this job.");
                jobs.Add(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _logger.LogWarning("Skipped job {JobId}: its job file cannot be read ({ExceptionType}).", id, ex.GetType().Name);
            }
        }

        jobs.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        return jobs;
    }

    /// <summary>Writes the job file whole: afterwards it holds this record, or, if this throws, the previous one.</summary>
    public void Save(JobRecord job) =>
        AtomicFileWriter.WriteAllText(JobPath(job.Id, JobFile).FullPath, JsonSerializer.Serialize(job, JobJson.Options));

    public void SaveResult(string jobId, string json) =>
        AtomicFileWriter.WriteAllText(JobPath(jobId, ResultFile).FullPath, json);

    public string? ReadResult(string jobId)
    {
        var file = JobPath(jobId, ResultFile);
        return File.Exists(file.FullPath) ? File.ReadAllText(file.FullPath, Utf8) : null;
    }

    public bool HasResult(string jobId) => File.Exists(JobPath(jobId, ResultFile).FullPath);

    /// <summary>Opens the job's log for appending; one writer per running job.</summary>
    public JobLogFile OpenLog(string jobId) => new(JobPath(jobId, LogFile).FullPath);

    /// <summary>The last <paramref name="maxLines"/> lines of a job's log, as written (paths unredacted).</summary>
    public IReadOnlyList<string> ReadLogTail(string jobId, int maxLines)
    {
        var file = JobPath(jobId, LogFile);
        if (maxLines <= 0 || !File.Exists(file.FullPath))
            return Array.Empty<string>();

        using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Utf8);
        var lines = reader.ReadToEnd().Split('\n').ToList();
        if (start > 0 && lines.Count > 0)
            lines.RemoveAt(0); // begins mid-line
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines.Skip(Math.Max(0, lines.Count - maxLines)).Select(l => l.TrimEnd('\r')).ToList();
    }

    private ResolvedPath JobPath(string jobId, string file)
    {
        if (!IsJobId(jobId))
            throw new ArgumentException("Not a job id.", nameof(jobId));
        return _resolver.ResolveServerPath(ServerFolder.State, $"{JobsFolder}/{jobId}/{file}");
    }

    /// <summary>A job's log, open for appending while the job runs.</summary>
    public sealed class JobLogFile : IDisposable
    {
        private readonly FileStream _stream;

        internal JobLogFile(string path)
        {
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        }

        public void Append(IReadOnlyList<string> lines)
        {
            var text = new StringBuilder();
            foreach (string line in lines)
                text.Append(line).Append('\n');
            _stream.Write(Utf8.GetBytes(text.ToString()));
            _stream.Flush();
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>Who holds a lock file: written into it, read back by the next process that finds it.</summary>
    internal sealed record ProcessStamp(int ProcessId, string Machine, string? JobId, DateTimeOffset AcquiredUtc)
    {
        public static ProcessStamp Current(string? jobId = null) =>
            new(Environment.ProcessId, Environment.MachineName, jobId, DateTimeOffset.UtcNow);

        /// <summary>The stamp in a lock file, or null when it is unreadable (a writer killed mid-write).</summary>
        public static ProcessStamp? Read(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8);
                return JsonSerializer.Deserialize<ProcessStamp>(reader.ReadToEnd(), JobJson.Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the stamp's process is another one that is still running. A stamp from this
        /// process is not: one server per process, so its own leftovers are stale. A stamp from
        /// another machine, or from a process this one may not inspect, cannot be checked and
        /// counts as live: a lock is never broken on a guess.
        /// </summary>
        public bool IsAnotherLiveProcess()
        {
            if (!string.Equals(Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ProcessId == Environment.ProcessId)
                return false;
            Process process;
            try
            {
                process = Process.GetProcessById(ProcessId);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false; // no such process
            }

            using (process)
            {
                try
                {
                    return !process.HasExited;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or InvalidOperationException)
                {
                    return true;
                }
            }
        }
    }
}
