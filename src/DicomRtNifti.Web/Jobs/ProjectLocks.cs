using System.Text;
using System.Text.Json;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Jobs;

/// <summary>Thrown when a job's output project is held by another job or process.</summary>
public sealed class ProjectLockedException : Exception
{
    public ProjectLockedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// One writer per output project (plan section 3.2): a job that writes a project holds
/// <c>OutputRoot/&lt;project&gt;/.dicomrtnifti-job.lock</c> for as long as it runs, so a second
/// job, here or in another server process, cannot interleave writes into one manifest or one
/// anonymization key. The file records the holder's job and process; it is deleted when the job
/// ends however it ends, by the OS on Windows when the process dies, and otherwise by restart
/// recovery (<see cref="ReleaseStale"/>) or by the next acquirer once the process is gone.
///
/// The lock is advisory (risk R-8): the desktop GUI does not take it. Every path here comes from
/// <see cref="DataRootResolver.ResolveServerPath"/>; this is one of the few types allowed to touch
/// the filesystem (the architecture test lists them).
/// </summary>
public sealed class ProjectLocks
{
    public const string LockFileName = ".dicomrtnifti-job.lock";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Process-wide, by lock-file path: the locks this process holds, whichever host took them.
    private static readonly Dictionary<string, string> Held = new(StringComparer.FromComparison(DataRootResolver.PathComparison));

    private readonly DataRootResolver _resolver;
    private readonly DataRoots _roots;

    public ProjectLocks(DataRootResolver resolver, DataRoots roots)
    {
        _resolver = resolver;
        _roots = roots;
    }

    /// <summary>
    /// The project folder under <c>OutputRoot</c>, checked: one plain name, no separators.
    /// </summary>
    /// <exception cref="JobRefusedException">No output root, or a name that is not one safe folder name.</exception>
    public ResolvedPath ResolveProject(string? project)
    {
        if (_roots.Folder(ServerFolder.Output) is null)
            throw new JobRefusedException("Jobs that write a project need Web:OutputRoot.");
        if (string.IsNullOrWhiteSpace(project) || project.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw new JobRefusedException("A project is one folder name under the output root.");
        try
        {
            return _resolver.ResolveServerPath(ServerFolder.Output, project);
        }
        catch (PathRefusedException ex)
        {
            throw new JobRefusedException("The project name is refused: " + ex.Message);
        }
    }

    /// <summary>The job that holds <paramref name="project"/> in this process, or null.</summary>
    public string? HolderOf(string project)
    {
        string path = LockPath(project).FullPath;
        lock (Held)
            return Held.TryGetValue(path, out string? jobId) ? jobId : null;
    }

    /// <summary>Takes the project for <paramref name="jobId"/>, creating the project folder if needed.</summary>
    /// <exception cref="ProjectLockedException">Another job or process holds it.</exception>
    public Handle Acquire(string project, string jobId)
    {
        var folder = ResolveProject(project);
        var lockPath = LockPath(project);
        string presented = lockPath.Presented;

        lock (Held)
        {
            if (Held.TryGetValue(lockPath.FullPath, out string? holder))
                throw new ProjectLockedException($"Project '{project}' is in use by job {holder}.");

            Directory.CreateDirectory(folder.FullPath);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var stream = new FileStream(lockPath.FullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
                    try
                    {
                        stream.Write(Utf8.GetBytes(JsonSerializer.Serialize(JobStore.ProcessStamp.Current(jobId), JobJson.Compact)));
                        stream.Flush(true);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }

                    Held[lockPath.FullPath] = jobId;
                    return new Handle(lockPath.FullPath, stream);
                }
                catch (IOException) when (File.Exists(lockPath.FullPath))
                {
                    var stamp = JobStore.ProcessStamp.Read(lockPath.FullPath);
                    if (stamp is null)
                        throw new ProjectLockedException(
                            $"Project '{project}' has a lock file that cannot be read ({presented}). Delete it if no other process is writing this project.");
                    if (stamp.IsAnotherLiveProcess())
                        throw new ProjectLockedException(
                            $"Project '{project}' is in use by process {stamp.ProcessId} on {stamp.Machine} (job {stamp.JobId}).");
                    // Left by a process that is gone, or by this one before it lost track of it.
                    File.Delete(lockPath.FullPath);
                }
            }

            throw new ProjectLockedException($"Project '{project}' could not be locked ({presented}).");
        }
    }

    /// <summary>
    /// Restart recovery: deletes the project's lock file when it names <paramref name="jobId"/>,
    /// a job this server ran and that is no longer running. Returns whether it did.
    /// </summary>
    public bool ReleaseStale(string project, string jobId)
    {
        ResolvedPath lockPath;
        try
        {
            lockPath = LockPath(project);
        }
        catch (JobRefusedException)
        {
            return false;
        }

        lock (Held)
        {
            if (Held.ContainsKey(lockPath.FullPath) || !File.Exists(lockPath.FullPath))
                return false;
            var stamp = JobStore.ProcessStamp.Read(lockPath.FullPath);
            if (stamp?.JobId != jobId)
                return false;
            File.Delete(lockPath.FullPath);
            return true;
        }
    }

    /// <summary>Whether the project's lock file exists (held here, elsewhere, or stale).</summary>
    public bool LockFileExists(string project) => File.Exists(LockPath(project).FullPath);

    private ResolvedPath LockPath(string project)
    {
        ResolveProject(project);
        return _resolver.ResolveServerPath(ServerFolder.Output, project + "/" + LockFileName);
    }

    /// <summary>A held project; disposing it releases the project and deletes its lock file.</summary>
    public sealed class Handle : IDisposable
    {
        private readonly string _path;
        private FileStream? _stream;

        internal Handle(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        public void Dispose()
        {
            lock (Held)
            {
                if (_stream is null)
                    return;
                _stream.Dispose(); // DeleteOnClose removes the file
                _stream = null;
                Held.Remove(_path);
            }
        }
    }
}
