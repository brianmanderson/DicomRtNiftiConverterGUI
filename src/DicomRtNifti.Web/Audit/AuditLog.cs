using System.Text;
using System.Text.Json;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DicomRtNifti.Web.Audit;

/// <summary>What an audit record says happened (plan section 6, "Audit log").</summary>
public static class AuditActions
{
    public const string JobSubmitted = "job.submit";
    public const string JobRefused = "job.refuse";
    public const string JobStarted = "job.start";
    public const string JobCancelRequested = "job.cancel";
    public const string JobRetried = "job.retry";
    public const string JobFinished = "job.finish";
    public const string JobInterrupted = "job.interrupt";

    public static readonly IReadOnlyList<string> All = new[]
    {
        JobSubmitted, JobRefused, JobStarted, JobCancelRequested, JobRetried, JobFinished, JobInterrupted,
    };
}

/// <summary>
/// One line of the audit log: who, what, when, on which project and job, with which command, and
/// how it came out. Text fields are redacted as <see cref="PathRedactor"/> does before they are
/// written, so a server path appears as its root token; root-relative paths stay, which is why
/// the audit log has the job log's protection (it is PHI).
/// </summary>
public sealed record AuditEntry
{
    public DateTimeOffset TimeUtc { get; init; }

    public required string User { get; init; }

    public required string Action { get; init; }

    public string? JobId { get; init; }

    public string? Kind { get; init; }

    public string? Project { get; init; }

    /// <summary>The job's title or the thing acted on, as root tokens.</summary>
    public string? Target { get; init; }

    /// <summary>The CLI-equivalent command.</summary>
    public string? Cli { get; init; }

    /// <summary>The job's state after the action, or "refused".</summary>
    public string? Outcome { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The append-only audit log, <c>StateRoot/audit/audit-YYYY-MM-DD.jsonl</c> (UTC days), one JSON
/// object per line. Nothing in the app rewrites or deletes a line. Every path here comes from
/// <see cref="DataRootResolver.ResolveServerPath"/>; this is one of the few types allowed to
/// touch the filesystem (the architecture test lists them).
/// </summary>
public sealed class AuditLog
{
    public const string AuditFolder = "audit";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DataRoots _roots;
    private readonly DataRootResolver _resolver;
    private readonly PathRedactor _redactor;
    private readonly object _write = new();

    public AuditLog(DataRoots roots, DataRootResolver resolver, PathRedactor redactor)
    {
        _roots = roots;
        _resolver = resolver;
        _redactor = redactor;
    }

    public bool IsConfigured => _roots.Folder(ServerFolder.State) is not null;

    /// <summary>Appends one record, redacted and stamped now if it has no time.</summary>
    /// <exception cref="IOException">The log cannot be written; the caller decides whether the action may go ahead.</exception>
    public void Write(AuditEntry entry)
    {
        if (!IsConfigured)
            throw new IOException("The audit log needs Web:StateRoot.");

        var time = entry.TimeUtc == default ? DateTimeOffset.UtcNow : entry.TimeUtc.ToUniversalTime();
        var redacted = entry with
        {
            TimeUtc = time,
            User = _redactor.Redact(entry.User),
            Target = Redact(entry.Target),
            Cli = Redact(entry.Cli),
            Detail = Redact(entry.Detail),
            Project = Redact(entry.Project),
        };
        byte[] line = Utf8.GetBytes(JsonSerializer.Serialize(redacted, JobJson.Compact) + "\n");

        var file = _resolver.ResolveServerPath(ServerFolder.State, $"{AuditFolder}/audit-{time:yyyy-MM-dd}.jsonl");
        lock (_write)
        {
            Directory.CreateDirectory(_resolver.ResolveServerPath(ServerFolder.State, AuditFolder).FullPath);
            using var stream = new FileStream(file.FullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line);
            stream.Flush(true);
        }
    }

    /// <summary>
    /// The newest records first, at most <paramref name="max"/>, optionally of one action. A line
    /// that does not parse (the torn end of a write cut off by a kill) is skipped.
    /// </summary>
    public IReadOnlyList<AuditEntry> ReadRecent(int max, string? action = null)
    {
        var entries = new List<AuditEntry>();
        if (!IsConfigured || max <= 0)
            return entries;

        var folder = _resolver.ResolveServerPath(ServerFolder.State, AuditFolder);
        if (!Directory.Exists(folder.FullPath))
            return entries;

        var days = Directory.EnumerateFiles(folder.FullPath, "audit-*.jsonl")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderByDescending(name => name, StringComparer.Ordinal);
        foreach (string name in days)
        {
            var file = _resolver.ResolveServerPath(ServerFolder.State, AuditFolder + "/" + name);
            List<string> lines;
            using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Utf8))
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                AuditEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<AuditEntry>(lines[i], JobJson.Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry is null || (action is not null && entry.Action != action))
                    continue;
                entries.Add(entry);
                if (entries.Count >= max)
                    return entries;
            }
        }

        return entries;
    }

    private string? Redact(string? text) => text is null ? null : _redactor.Redact(text);
}

/// <summary>Registers the audit log and the current-user service; safe to call more than once.</summary>
public static class AuditServices
{
    public static void Add(IServiceCollection services)
    {
        services.TryAddSingleton<AuditLog>();
        services.TryAddSingleton<ICurrentUser, LocalUser>();
    }
}
