using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Hosting;

/// <summary>A configured folder: its id (what the browser and the logs see) and its server path.</summary>
/// <param name="Path">Normalized absolute path without a trailing separator (except a filesystem root).</param>
public sealed record DataRoot(string Id, string Path, RootAccess Access);

/// <summary>The server folders that are not data roots. Only <see cref="Output"/> is browsable.</summary>
public enum ServerFolder
{
    Output,
    Keys,
    Uploads,
    State,
}

/// <summary>
/// The roots of this server, built once from <see cref="WebOptions"/>: the data roots the browser
/// can name, in configuration order, then the output root as <c>output</c>; and the four server
/// folders by kind. <see cref="DataRootResolver"/> is the only code that turns one of these plus a
/// relative path into a filesystem path.
/// </summary>
public sealed class DataRoots
{
    public const string OutputId = "output";

    /// <summary>Ids that name server folders, so a data root may not take them.</summary>
    public static readonly IReadOnlySet<string> ReservedIds =
        new HashSet<string>(StringComparer.Ordinal) { OutputId, "keys", "uploads", "state" };

    private readonly Dictionary<string, DataRoot> _byId;
    private readonly Dictionary<ServerFolder, DataRoot> _serverFolders;

    public DataRoots(IOptions<WebOptions> options)
        : this(options.Value)
    {
    }

    // Not a constructor: the container must see exactly one it can call.
    public static DataRoots From(WebOptions options) => new(options);

    private DataRoots(WebOptions options)
    {
        var browsable = new List<DataRoot>();
        foreach (var root in options.DataRoots)
            browsable.Add(new DataRoot(root.Id, DataRootResolver.NormalizeRootPath(root.Path), root.Access));

        _serverFolders = new Dictionary<ServerFolder, DataRoot>();
        AddFolder(ServerFolder.Output, OutputId, options.OutputRoot);
        AddFolder(ServerFolder.Keys, "keys", options.KeyRoot);
        AddFolder(ServerFolder.Uploads, "uploads", options.UploadRoot);
        AddFolder(ServerFolder.State, "state", options.StateRoot);

        if (_serverFolders.TryGetValue(ServerFolder.Output, out var output))
            browsable.Add(output);

        Browsable = browsable;
        _byId = browsable.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    /// <summary>The roots the browser may name, data roots first, then <c>output</c> when configured.</summary>
    public IReadOnlyList<DataRoot> Browsable { get; }

    /// <summary>The root with exactly this id (ordinal: ids are lower-case by validation), or null.</summary>
    public DataRoot? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var root) ? root : null;

    /// <summary>The server folder of this kind, or null when it is not configured.</summary>
    public DataRoot? Folder(ServerFolder folder) =>
        _serverFolders.TryGetValue(folder, out var root) ? root : null;

    /// <summary>Every configured folder, browsable or not: what <see cref="PathRedactor"/> rewrites to an id.</summary>
    public IEnumerable<DataRoot> AllFolders => Browsable.Concat(
        _serverFolders.Where(f => f.Key != ServerFolder.Output).Select(f => f.Value));

    private void AddFolder(ServerFolder folder, string id, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _serverFolders[folder] = new DataRoot(id, DataRootResolver.NormalizeRootPath(path), RootAccess.ReadWrite);
    }
}
