using System.Reflection;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;

namespace DicomRtNifti.Web.Features.Export;

/// <summary>
/// The export a session describes, as Core's command line: the command the page shows
/// (<see cref="Cli"/>) and the job that runs it (<see cref="Parameters"/>), or why there is none
/// yet (<see cref="Problems"/>). The command is <see cref="CohortCommandLine.Render(CohortCommand, PathPresenter)"/>
/// of the session's options and validated by parsing it back with Core's grammar, so the web
/// adds no rule of its own; the job's arguments are that same command less the folders the job
/// sets itself.
/// </summary>
public sealed record ExportCommand(
    CohortMode Mode,
    string? Cli,
    CohortJobParameters? Parameters,
    IReadOnlyList<string> Problems,
    ProjectSalt? Salt)
{
    public bool IsValid => Parameters is not null && Problems.Count == 0;
}

/// <summary>
/// Turns an <see cref="ExportSession"/> into the <see cref="ExportCommand"/> for a conversion or a
/// survey. The project's salt (D-36) comes from <see cref="ProjectStore.SaltFor"/>, never from the
/// form, and the command the page shows names it as <see cref="SaltToken"/>, not its value: the
/// salt is recorded only in the project's key file.
/// </summary>
public sealed class ExportCommandBuilder
{
    /// <summary>What the shown command carries in place of the project's salt.</summary>
    public const string SaltToken = "$PROJECT_SALT";

    /// <summary>The flags (each with its value) the job sets from its folders; never in its arguments.</summary>
    private static readonly string[] JobFolderFlags = { "--input", "--output", "--key-path", "--associations", "--json-out" };

    private readonly ProjectStore _projects;
    private readonly DataRootResolver _resolver;
    private readonly RootPathPresenter _presenter;

    public ExportCommandBuilder(ProjectStore projects, DataRootResolver resolver, RootPathPresenter presenter)
    {
        _projects = projects;
        _resolver = resolver;
        _presenter = presenter;
    }

    /// <param name="mode"><see cref="CohortMode.Convert"/> or <see cref="CohortMode.Manifest"/> (a survey).</param>
    /// <param name="dryRun">Show the command with <c>--dry-run</c> (Plan review); the job's arguments never carry it.</param>
    /// <param name="createSalt">
    /// Under anonymization, give a project with no key its random salt now (the job is about to be
    /// submitted). Without it a new project's salt shows as pending and nothing is written.
    /// </param>
    public ExportCommand Build(ExportSession session, IInventorySelection selection, CohortMode mode, bool dryRun = false, bool createSalt = false)
    {
        if (mode is not (CohortMode.Convert or CohortMode.Manifest))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var problems = new List<string>();
        if (!_projects.IsConfigured)
            problems.Add("Exports need Web:OutputRoot and Web:StateRoot on the server.");

        ResolvedPath? input = null;
        if (session.InputRootId is null)
            problems.Add("Choose the cohort folder.");
        else if (!_resolver.TryResolve(session.InputRootId, session.InputPath, RootAccess.Read, out input, out string? refusal))
            problems.Add(refusal);

        string? project = session.Project;
        if (string.IsNullOrWhiteSpace(project))
            problems.Add("Choose or name the output project.");
        else if (_projects.CheckProjectName(project) is { } refusedProject)
            problems.Add(refusedProject);

        var options = ForMode(session.Options, mode);
        options.PatientIds = selection.PatientIds.ToList();
        options.SeriesInstanceUids = selection.SeriesInstanceUids.ToList();
        options.StructureSetUids = selection.StructureSetUids.ToList();
        options.Associations = new List<RoiAssociation>();
        options.KeyPath = null;
        options.Salt = new CohortExportOptions().Salt;

        if (problems.Count > 0)
            return new ExportCommand(mode, null, null, problems, null);

        options.InputRoot = input!.FullPath;
        options.OutputRoot = _resolver.ResolveServerPath(ServerFolder.Output, project!).FullPath;

        var command = new CohortCommand { Mode = mode, Options = options };
        string? associationsFile = null;
        if (session.AssociationSet is { } set)
        {
            if (ProjectStore.CheckSetName(set) is { } refusedSet)
                problems.Add(refusedSet);
            else if (!_projects.AssociationSetExists(project!, set))
                problems.Add($"The association set '{set}' has not been saved in project {project}.");
            else
            {
                try
                {
                    options.Associations = _projects.LoadAssociations(project!, set);
                    command.AssociationsPath = _projects.AssociationsPath(project!, set).FullPath;
                    associationsFile = ProjectStore.AssociationsFile(project!, set);
                }
                catch (InvalidDataException)
                {
                    problems.Add($"The association set '{set}' does not parse; fix or replace it on ROI curation.");
                }
            }
        }

        ProjectSalt? salt = null;
        if (options.Anonymize)
        {
            salt = _projects.SaltFor(project!, createSalt);
            if (salt.Problem is not null)
                problems.Add(salt.Problem);
            options.Salt = salt.Salt ?? SaltToken;
            options.KeyPath = _projects.CanAnonymize ? _projects.KeyPath(project!).FullPath : null;
        }

        if (problems.Count > 0)
            return new ExportCommand(mode, null, null, problems, salt);

        string[] argv;
        try
        {
            argv = CohortCommandLine.Render(command, CohortDocuments.Identity);
            // Core's grammar is the validator: what it would refuse on the command line, the page refuses.
            CohortCommandLine.Parse(mode, argv);
        }
        catch (Exception ex) when (ex is ArgumentException or CommandLineUsageException)
        {
            return new ExportCommand(mode, null, null, new[] { ex.Message }, salt);
        }

        var display = new CohortCommand
        {
            Mode = mode,
            Options = options,
            AssociationsPath = command.AssociationsPath,
            DryRun = dryRun,
        };
        string cli = CommandLineText.Join(MaskSalt(CohortCommandLine.Render(display, _presenter.Commands)));
        // A new project's salt does not exist until it is submitted (createSalt): until then the
        // command shows, and nothing runs.
        var parameters = salt?.Pending == true
            ? null
            : new CohortJobParameters(session.InputRootId!, session.InputPath, project!, JobArguments(argv), associationsFile);
        return new ExportCommand(mode, cli, parameters, Array.Empty<string>(), salt);
    }

    /// <summary>
    /// The arguments a cohort job takes: a rendered command line less its mode flag and the
    /// folders the job sets itself (each flag with its value), in Core's order.
    /// </summary>
    public static IReadOnlyList<string> JobArguments(IReadOnlyList<string> argv)
    {
        var arguments = new List<string>();
        for (int i = 1; i < argv.Count; i++)
        {
            if (JobFolderFlags.Contains(argv[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (!string.Equals(argv[i], CohortCommandLine.DryRunFlag, StringComparison.OrdinalIgnoreCase))
                arguments.Add(argv[i]);
        }

        return arguments;
    }

    /// <summary>A command line with the value of <c>--salt</c> replaced by <see cref="SaltToken"/>.</summary>
    public static string[] MaskSalt(IReadOnlyList<string> argv)
    {
        var masked = argv.ToArray();
        for (int i = 0; i < masked.Length - 1; i++)
        {
            if (string.Equals(masked[i], "--salt", StringComparison.OrdinalIgnoreCase))
                masked[i + 1] = SaltToken;
        }

        return masked;
    }

    /// <summary>
    /// A copy of <paramref name="options"/> holding only what <paramref name="mode"/> has flags
    /// for: a survey writes no images, masks, doses or metadata, and a conversion always
    /// computes volumes, so the toggles of the other mode go back to Core's defaults.
    /// </summary>
    public static CohortExportOptions ForMode(CohortExportOptions options, CohortMode mode)
    {
        var copy = Copy(options);
        var defaults = new CohortExportOptions();
        if (mode == CohortMode.Manifest)
        {
            copy.ExportImages = defaults.ExportImages;
            copy.ExportStructures = defaults.ExportStructures;
            copy.ExportDoses = defaults.ExportDoses;
            copy.ImageKeywords = new List<string>();
            copy.StructureKeywords = new List<string>();
            copy.DoseKeywords = new List<string>();
        }
        else
        {
            copy.ComputeVolumes = defaults.ComputeVolumes;
        }

        return copy;
    }

    /// <summary>
    /// A deep copy of every settable property, by reflection, so an option Core adds later is
    /// carried without this file changing (lists and arrays are copied, not shared).
    /// </summary>
    public static CohortExportOptions Copy(CohortExportOptions options)
    {
        var copy = new CohortExportOptions();
        foreach (var property in typeof(CohortExportOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
                continue;
            object? value = property.GetValue(options);
            value = value switch
            {
                List<string> list => new List<string>(list),
                List<RoiAssociation> list => list.Select(a => new RoiAssociation
                {
                    CanonicalName = a.CanonicalName,
                    Aliases = new List<string>(a.Aliases ?? new List<string>()),
                }).ToList(),
                double[] array => (double[])array.Clone(),
                _ => value,
            };
            property.SetValue(copy, value);
        }

        return copy;
    }
}
