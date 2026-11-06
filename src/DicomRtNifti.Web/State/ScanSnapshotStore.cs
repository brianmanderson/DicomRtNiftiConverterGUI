using System.Text.Json;
using System.Text.Json.Serialization;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DicomRtNifti.Web.State;

/// <summary>
/// One scan the Data sources page ran, as every later page reads it: Core's scanned tree, the
/// folder it came from as a root id and a root-relative path, and the series the user selected.
/// Addressed by an opaque id (the scan job's), never by a path or an identifier.
///
/// The tree is PHI-bearing server state by design (plan section 6): it holds names, MRNs, UIDs
/// and full file paths. It is kept in memory and in <c>StateRoot/scans/</c>, and shown only
/// through the pages.
/// </summary>
public sealed class ScanSnapshot
{
    internal ScanSnapshot(string id, ResolvedPath folder, DateTimeOffset scannedUtc, DicomScanResult scan)
    {
        Id = id;
        Folder = folder;
        ScannedUtc = scannedUtc;
        Scan = scan;
    }

    /// <summary>The scan job's id.</summary>
    public string Id { get; }

    /// <summary>The scanned folder, resolved through the data roots when the snapshot was made or loaded.</summary>
    public ResolvedPath Folder { get; }

    public DateTimeOffset ScannedUtc { get; }

    /// <summary>
    /// The tree as <c>DicomScannerService</c> returned it, with its aliasing intact: an image
    /// series' linked RT objects are the same instances as the study's members, which is what
    /// <c>CohortExportService.BuildPlan</c> compares by reference.
    /// </summary>
    public DicomScanResult Scan { get; }

    /// <summary>The Inventory page's selection, which Export setup reads. In memory only.</summary>
    public ScanSelection Selection { get; } = new();
}

/// <summary>
/// The series a user picked in the Inventory, by SeriesInstanceUID. This is what Export setup hands
/// Core as <c>CohortExportOptions.SeriesInstanceUids</c> (W4-C). An empty selection means the whole
/// scan. Safe to use from several circuits at once.
/// </summary>
public sealed class ScanSelection
{
    private readonly object _gate = new();
    private readonly HashSet<string> _series = new(StringComparer.Ordinal);

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
                return _series.Count;
        }
    }

    public bool IsEmpty => Count == 0;

    public bool Contains(string seriesInstanceUid)
    {
        lock (_gate)
            return _series.Contains(seriesInstanceUid);
    }

    /// <summary>Selects or deselects every series given, in one change.</summary>
    public void Set(IEnumerable<string> seriesInstanceUids, bool selected)
    {
        lock (_gate)
        {
            foreach (string uid in seriesInstanceUids)
            {
                if (selected)
                    _series.Add(uid);
                else
                    _series.Remove(uid);
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
            _series.Clear();
        Changed?.Invoke();
    }

    /// <summary>The selected UIDs, ordinal order, for <c>CohortExportOptions.SeriesInstanceUids</c>.</summary>
    public List<string> SeriesInstanceUids()
    {
        lock (_gate)
            return _series.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Which snapshot this circuit is looking at. Scoped: one per browser tab. With no choice made it
/// falls back to the store's most recent snapshot, so a reload lands where the user was (one
/// user, loopback, D-33).
/// </summary>
public sealed class ScanSession
{
    private readonly ScanSnapshotStore _store;
    private string? _snapshotId;

    public ScanSession(ScanSnapshotStore store)
    {
        _store = store;
    }

    public string? SnapshotId => _snapshotId ?? _store.LatestId;

    /// <summary>The snapshot in view, or null when there is none (or it can no longer be loaded).</summary>
    public ScanSnapshot? Current => SnapshotId is { } id ? _store.Find(id) : null;

    public void Open(string snapshotId)
    {
        _snapshotId = snapshotId;
        _store.MarkLatest(snapshotId);
    }
}

/// <summary>
/// What a later page reads of the scan state (Export setup, Plan review): a snapshot by its id, the
/// tree <c>CohortExportService.BuildPlan</c> takes, and its selection. Injected instead of the store,
/// so a reader never writes.
/// </summary>
public interface IScanSnapshots
{
    /// <summary>The snapshot saved or opened last, by any circuit.</summary>
    string? LatestId { get; }

    /// <summary>The snapshot, from memory or its file; null when there is none or it cannot be used.</summary>
    ScanSnapshot? Find(string? id);
}

/// <summary>
/// Keeps scan snapshots: in memory (the few most recent) and, when a state root is configured,
/// as <c>StateRoot/scans/&lt;id&gt;.json</c>, so the Inventory survives a restart. The file is the
/// <see cref="DicomScanResult"/> serialized with reference preservation, because the model's
/// linked-series lists alias study members; a test proves <c>BuildPlan</c> on a restored snapshot
/// equals <c>BuildPlan</c> on the original (plan section 4.2).
///
/// The file records the folder as root id and relative path, not as a server path: a snapshot is
/// re-resolved through the data roots when it is loaded, and one whose root is gone is not loaded.
/// Every path here comes from <see cref="DataRootResolver.ResolveServerPath"/>.
/// </summary>
public sealed class ScanSnapshotStore : IScanSnapshots
{
    public const string ScansFolder = "scans";
    public const string Schema = "dicomrtnifti.web-scan-snapshot/1";

    /// <summary>Snapshots held in memory; older ones are read back from their file when asked for.</summary>
    public const int MemoryCapacity = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        ReferenceHandler = ReferenceHandler.Preserve,
        WriteIndented = false,
    };

    private readonly DataRoots _roots;
    private readonly DataRootResolver _resolver;
    private readonly ILogger<ScanSnapshotStore> _logger;
    private readonly object _gate = new();
    private readonly LinkedList<ScanSnapshot> _recent = new();
    private string? _latestId;

    public ScanSnapshotStore(DataRoots roots, DataRootResolver resolver, ILogger<ScanSnapshotStore> logger)
    {
        _roots = roots;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>True when snapshots are written to disk; otherwise a restart asks for a re-scan.</summary>
    public bool IsPersistent => _roots.Folder(ServerFolder.State) is not null;

    public string? LatestId
    {
        get
        {
            lock (_gate)
                return _latestId;
        }
    }

    /// <summary>Keeps <paramref name="scan"/> as snapshot <paramref name="id"/> and makes it the latest.</summary>
    public ScanSnapshot Save(string id, ResolvedPath folder, DicomScanResult scan, DateTimeOffset scannedUtc)
    {
        RequireId(id);
        var snapshot = new ScanSnapshot(id, folder, scannedUtc, scan);
        if (IsPersistent)
        {
            var file = new SnapshotFile
            {
                Schema = Schema,
                Id = id,
                RootId = folder.Root.Id,
                RelativePath = folder.RelativePath,
                ScannedUtc = scannedUtc,
                Scan = scan,
            };
            var path = SnapshotPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path.FullPath)!);
            string temp = path.FullPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
            File.Move(temp, path.FullPath, overwrite: true);
        }

        Remember(snapshot);
        MarkLatest(id);
        return snapshot;
    }

    public ScanSnapshot? Find(string? id)
    {
        if (!JobStore.IsJobId(id))
            return null;
        lock (_gate)
        {
            if (_recent.FirstOrDefault(s => s.Id == id) is { } held)
                return held;
        }

        var loaded = Load(id!);
        if (loaded is not null)
            Remember(loaded);
        return loaded;
    }

    public void MarkLatest(string id)
    {
        lock (_gate)
            _latestId = id;
    }

    /// <summary>Serializes a tree the way the store writes it, for the round-trip test.</summary>
    internal static string SerializeScan(DicomScanResult scan) => JsonSerializer.Serialize(scan, Json);

    internal static DicomScanResult DeserializeScan(string json) =>
        JsonSerializer.Deserialize<DicomScanResult>(json, Json)
        ?? throw new JsonException("The snapshot holds no scan.");

    private ScanSnapshot? Load(string id)
    {
        if (!IsPersistent)
            return null;
        var path = SnapshotPath(id);
        if (!File.Exists(path.FullPath))
            return null;
        try
        {
            var file = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path.FullPath), Json);
            if (file is null || file.Schema != Schema || file.Id != id || file.Scan is null)
            {
                _logger.LogWarning("Scan snapshot {SnapshotId} has another schema and was not loaded.", id);
                return null;
            }

            if (!_resolver.TryResolve(file.RootId, file.RelativePath, RootAccess.Read, out var folder, out _))
            {
                _logger.LogWarning("Scan snapshot {SnapshotId} names a data root that is no longer configured.", id);
                return null;
            }

            return new ScanSnapshot(id, folder, file.ScannedUtc, file.Scan);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("Scan snapshot {SnapshotId} could not be read ({ExceptionType}).", id, ex.GetType().Name);
            return null;
        }
    }

    private void Remember(ScanSnapshot snapshot)
    {
        lock (_gate)
        {
            var existing = _recent.FirstOrDefault(s => s.Id == snapshot.Id);
            if (existing is not null)
                _recent.Remove(existing);
            _recent.AddFirst(snapshot);
            while (_recent.Count > MemoryCapacity)
                _recent.RemoveLast();
        }
    }

    private ResolvedPath SnapshotPath(string id) =>
        _resolver.ResolveServerPath(ServerFolder.State, ScansFolder + "/" + id + ".json");

    private static void RequireId(string id)
    {
        if (!JobStore.IsJobId(id))
            throw new ArgumentException("A snapshot id is a job id.", nameof(id));
    }

    private sealed class SnapshotFile
    {
        public string? Schema { get; set; }

        public string? Id { get; set; }

        public string? RootId { get; set; }

        public string? RelativePath { get; set; }

        public DateTimeOffset ScannedUtc { get; set; }

        public DicomScanResult? Scan { get; set; }
    }
}

/// <summary>
/// Registers the scan state: the snapshot store and the per-circuit session. Idempotent, so every
/// page that reads a snapshot (Data sources, Inventory, Export setup) may call it from its own
/// <c>ConfigureServices</c>. The Data sources feature registers the job that fills the store.
/// </summary>
public static class ScanStateServices
{
    public static void Add(IServiceCollection services)
    {
        services.TryAddSingleton<ScanSnapshotStore>();
        services.TryAddSingleton<IScanSnapshots>(sp => sp.GetRequiredService<ScanSnapshotStore>());
        services.TryAddScoped<ScanSession>();
    }
}
