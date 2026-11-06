using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using Newtonsoft.Json;

namespace DicomRtNifti.Web.State;

/// <summary>
/// The project's own salt (decision D-36), or why the project cannot be anonymized.
/// <see cref="Salt"/> is null exactly when <see cref="Problem"/> is not; <see cref="Pending"/> marks
/// a project with no key yet, whose salt is generated when its first anonymized job is submitted.
/// </summary>
public sealed record ProjectSalt(string? Salt, string? Problem, bool Pending = false);

/// <summary>
/// What belongs to a project (plan sections 3.3 and 3.5): its ROI association sets and, under
/// anonymization, its salt. A project is one folder under <c>OutputRoot</c>; its association sets
/// live in the server's state, <c>StateRoot/projects/&lt;project&gt;/associations/&lt;set&gt;/roi_associations.json</c>,
/// the exact JSON <c>--associations</c> reads; its key lives at <c>KeyRoot/&lt;project&gt;/AnonymizationKey.json</c>.
///
/// Every read and write goes through Core: association sets through <see cref="SettingsService"/>
/// over the set's folder (atomic writes, and a file that does not parse is refused rather than
/// replaced), the key through <see cref="AnonymizationService.LoadKeyFile"/> and
/// <see cref="AnonymizationService.SaveKeyFile"/>. Every path comes from
/// <see cref="DataRootResolver.ResolveServerPath"/>, so this type calls no filesystem API of its own.
///
/// <b>The salt (D-36).</b> The public default salt, <c>DicomToNifti</c>, is reversible by
/// enumerating MRNs, so the web never offers it. A project's salt is drawn at random (128 bits)
/// when its first anonymized job is submitted and recorded only in its key file; every later job
/// reads it back from there. A key that records the public default salt (one the CLI or the
/// desktop app wrote with the default) is refused, not used.
/// </summary>
public sealed partial class ProjectStore
{
    /// <summary>The state folder that holds every project's association sets.</summary>
    public const string ProjectsFolder = "projects";

    public const string AssociationsFolder = "associations";

    /// <summary>The file name Core's <see cref="SettingsService"/> keeps an association set under.</summary>
    public const string AssociationsFileName = "roi_associations.json";

    // Core's own association-file format (SettingsService.ExportAssociations): indented Newtonsoft.
    private static readonly JsonSerializerSettings AssociationJson = new() { Formatting = Formatting.Indented };

    private readonly DataRoots _roots;
    private readonly DataRootResolver _resolver;
    private readonly ProjectLocks _locks;
    private readonly object _saltGate = new();

    public ProjectStore(DataRoots roots, DataRootResolver resolver, ProjectLocks locks)
    {
        _roots = roots;
        _resolver = resolver;
        _locks = locks;
    }

    /// <summary>True when the server has both an output root and a state root, which a project needs.</summary>
    public bool IsConfigured => _roots.Folder(ServerFolder.Output) is not null && _roots.Folder(ServerFolder.State) is not null;

    /// <summary>True when anonymized projects are possible: the key has a root outside the output.</summary>
    public bool CanAnonymize => _roots.Folder(ServerFolder.Keys) is not null;

    /// <summary>The projects that exist: the folders under <c>OutputRoot</c>.</summary>
    public IReadOnlyList<string> Projects()
    {
        if (_roots.Folder(ServerFolder.Output) is null)
            return Array.Empty<string>();
        var output = _resolver.ResolveServerPath(ServerFolder.Output, "");
        return DataRootResolver.DirectoryExists(output) ? _resolver.ListSubfolders(output) : Array.Empty<string>();
    }

    /// <summary>
    /// The refusal for a project name, or null when it is one safe folder name under the output
    /// root (the job's own check, <see cref="ProjectLocks.ResolveProject"/>).
    /// </summary>
    public string? CheckProjectName(string? project)
    {
        try
        {
            _locks.ResolveProject(project);
            return null;
        }
        catch (JobRefusedException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The refusal for an association-set name, or null when it is usable.</summary>
    public static string? CheckSetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "An association set needs a name.";
        return SetNamePattern().IsMatch(name)
            ? null
            : "An association set's name is 1 to 64 letters, digits, spaces, '.', '_' or '-', beginning and ending with a letter, digit, '_' or '-'.";
    }

    /// <summary>The project's association sets, by name.</summary>
    public IReadOnlyList<string> AssociationSets(string project)
    {
        var folder = SetsFolder(project);
        return DataRootResolver.DirectoryExists(folder) ? _resolver.ListSubfolders(folder) : Array.Empty<string>();
    }

    /// <summary>Reads an association set; an absent set is empty.</summary>
    /// <exception cref="InvalidDataException">The set's file exists but does not parse (Core's refusal).</exception>
    public List<RoiAssociation> LoadAssociations(string project, string set) =>
        Settings(project, set).LoadAssociations();

    /// <summary>Writes an association set, atomically, creating the project's folders as needed.</summary>
    public void SaveAssociations(string project, string set, IReadOnlyList<RoiAssociation> associations) =>
        Settings(project, set).SaveAssociations(associations.ToList());

    /// <summary>True when the set has been saved.</summary>
    public bool AssociationSetExists(string project, string set) => DataRootResolver.FileExists(AssociationsPath(project, set));

    /// <summary>
    /// The set's file relative to <c>StateRoot</c>: what a cohort job's
    /// <see cref="CohortJobParameters.AssociationsFile"/> names.
    /// </summary>
    public static string AssociationsFile(string project, string set) =>
        $"{ProjectsFolder}/{project}/{AssociationsFolder}/{set}/{AssociationsFileName}";

    /// <summary>The set's file on the server.</summary>
    public ResolvedPath AssociationsPath(string project, string set)
    {
        Check(project, set);
        return _resolver.ResolveServerPath(ServerFolder.State, AssociationsFile(project, set));
    }

    /// <summary>
    /// An association set as the JSON file <c>--associations</c> reads: the bytes Core writes for it
    /// (<see cref="SettingsService.SaveAssociations"/> and <see cref="SettingsService.ExportAssociations"/>
    /// use this one format).
    /// </summary>
    public static string ToJson(IReadOnlyList<RoiAssociation> associations) =>
        JsonConvert.SerializeObject(associations.ToList(), AssociationJson);

    /// <summary>
    /// Parses an imported association file as Core's <see cref="SettingsService.ImportAssociations"/>
    /// does. False with a reason when it does not parse; the caller then replaces nothing.
    /// </summary>
    public static bool TryParse(string json, out List<RoiAssociation> associations, out string? error)
    {
        associations = new List<RoiAssociation>();
        error = null;
        try
        {
            associations = JsonConvert.DeserializeObject<List<RoiAssociation>>(json) ?? new List<RoiAssociation>();
        }
        catch (JsonException ex)
        {
            error = "The file is not an association set: " + ex.Message;
            return false;
        }

        if (associations.Any(a => a is null || string.IsNullOrWhiteSpace(a.CanonicalName)))
        {
            associations = new List<RoiAssociation>();
            error = "The file is not an association set: every entry needs a CanonicalName.";
            return false;
        }

        foreach (var association in associations)
            association.Aliases ??= new List<string>();
        return true;
    }

    /// <summary>The project's key file: <c>KeyRoot/&lt;project&gt;/AnonymizationKey.json</c>.</summary>
    public ResolvedPath KeyPath(string project)
    {
        CheckProject(project);
        return _resolver.ResolveServerPath(ServerFolder.Keys, project + "/" + AnonymizationKeyLocation.FileName);
    }

    /// <summary>
    /// The project's salt, read from its key. With <paramref name="create"/> a project with no key
    /// yet gets one, holding a fresh random salt and no mappings; without, it is reported as
    /// <see cref="ProjectSalt.Pending"/>. A key under the public default salt, or one that does not
    /// parse, is a <see cref="ProjectSalt.Problem"/>.
    /// </summary>
    public ProjectSalt SaltFor(string project, bool create)
    {
        if (!CanAnonymize)
            return new ProjectSalt(null, "Anonymized projects need Web:KeyRoot: the key is never written into the output root.");
        if (CheckProjectName(project) is { } refused)
            return new ProjectSalt(null, refused);

        var key = KeyPath(project);
        lock (_saltGate)
        {
            AnonymizationKeyFile? file;
            try
            {
                file = AnonymizationService.LoadKeyFile(key.FullPath);
            }
            catch (InvalidDataException)
            {
                return new ProjectSalt(null, $"The project's anonymization key ({key.Presented}) does not parse; it needs a person to repair it.");
            }

            if (file is not null)
            {
                if (string.IsNullOrEmpty(file.Salt) || string.Equals(file.Salt, AnonymizationService.DefaultSalt, StringComparison.Ordinal))
                {
                    return new ProjectSalt(null,
                        $"The project's anonymization key ({key.Presented}) records the public default salt, whose hashes can be " +
                        "reversed by enumerating MRNs. The web does not anonymize under it: choose another project.");
                }
                return new ProjectSalt(file.Salt, null);
            }

            if (!create)
                return new ProjectSalt(null, null, Pending: true);

            string salt = NewSalt();
            AnonymizationService.SaveKeyFile(key.FullPath, new AnonymizationKeyFile { Salt = salt });
            return new ProjectSalt(salt, null);
        }
    }

    /// <summary>A random salt: 128 bits as 32 lower-case hex digits.</summary>
    public static string NewSalt() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    private SettingsService Settings(string project, string set)
    {
        var file = AssociationsPath(project, set);
        // The set's folder: the file's path without its name, as the resolver checked it.
        string folder = file.FullPath[..^AssociationsFileName.Length].TrimEnd('/', '\\');
        return new SettingsService(folder);
    }

    private ResolvedPath SetsFolder(string project)
    {
        CheckProject(project);
        return _resolver.ResolveServerPath(ServerFolder.State, $"{ProjectsFolder}/{project}/{AssociationsFolder}");
    }

    private void Check(string project, string set)
    {
        CheckProject(project);
        if (CheckSetName(set) is { } refused)
            throw new ArgumentException(refused, nameof(set));
    }

    private void CheckProject(string project)
    {
        if (CheckProjectName(project) is { } refused)
            throw new ArgumentException(refused, nameof(project));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-](?:[A-Za-z0-9 ._\-]{0,62}[A-Za-z0-9_\-])?$")]
    private static partial Regex SetNamePattern();
}
