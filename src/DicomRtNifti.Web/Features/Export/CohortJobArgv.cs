using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;

namespace DicomRtNifti.Web.Features.Export;

/// <summary>
/// The command line a cohort job of this feature group runs, assembled the way the job host's
/// own cohort jobs assemble theirs (<see cref="CohortJobHandler"/>): the mode flag, the folders
/// resolved from what the browser named (a data root and a root-relative path, a project under
/// <c>OutputRoot</c>, the key under <c>KeyRoot</c>, an association set under <c>StateRoot</c>),
/// then the job's arguments in Core's grammar, all parsed by <see cref="CohortCommandLine.Parse(CohortMode, string[], IProgress{string})"/>.
/// What Core refuses, and the flags the job sets itself, refuse the job before it is queued.
/// </summary>
public sealed class CohortJobArgv
{
    private readonly DataRootResolver _resolver;
    private readonly DataRoots _roots;
    private readonly ProjectLocks _locks;

    public CohortJobArgv(DataRootResolver resolver, DataRoots roots, ProjectLocks locks)
    {
        _resolver = resolver;
        _roots = roots;
        _locks = locks;
    }

    /// <summary>A command line, parsed, with the folders it names as the resolver checked them.</summary>
    public sealed record Built(CohortCommand Command, ResolvedPath Input, string? Project);

    /// <param name="project">The output project; null for <see cref="CohortMode.Analyze"/>, which writes nothing.</param>
    /// <param name="dryRun">Adds <c>--dry-run</c>: plan, print, write nothing.</param>
    /// <exception cref="JobRefusedException">A folder, a reserved flag or Core's grammar refuses the command.</exception>
    public Built Build(
        CohortMode mode, string? rootId, string? inputPath, string? project,
        IReadOnlyList<string>? arguments, string? associationsFile, bool dryRun, IProgress<string>? log)
    {
        if (!_resolver.TryResolve(rootId, inputPath, RootAccess.Read, out var input, out string? refusal))
            throw new JobRefusedException(refusal);

        string[] given = (arguments ?? Array.Empty<string>()).ToArray();
        foreach (string reserved in CohortJobHandler.ReservedFlags.Append(CohortCommandLine.AnalyzeFlag).Append(CohortCommandLine.CsvOutFlag))
        {
            if (CommandLineArgs.HasFlag(given, reserved))
                throw new JobRefusedException($"{reserved} is set by the job from its folders, not given as an argument.");
        }

        var argv = new List<string> { CohortCommandLine.FlagFor(mode), "--input", input.FullPath };
        if (mode != CohortMode.Analyze)
        {
            var output = _locks.ResolveProject(project);
            argv.Add("--output");
            argv.Add(output.FullPath);
            if (CommandLineArgs.HasFlag(given, "--anonymize"))
            {
                if (_roots.Folder(ServerFolder.Keys) is null)
                    throw new JobRefusedException("Anonymized jobs need Web:KeyRoot: the key is never written into the output root.");
                var key = _resolver.ResolveServerPath(ServerFolder.Keys, project + "/" + AnonymizationKeyLocation.FileName);
                argv.Add("--key-path");
                argv.Add(key.FullPath);
            }
        }
        else if (CommandLineArgs.HasFlag(given, "--anonymize"))
        {
            throw new JobRefusedException("This analysis shows the names as they are; it does not anonymize.");
        }

        if (associationsFile is not null)
        {
            ResolvedPath associations;
            try
            {
                associations = _resolver.ResolveServerPath(ServerFolder.State, associationsFile);
            }
            catch (Exception ex) when (ex is PathRefusedException or InvalidOperationException)
            {
                throw new JobRefusedException("The association set is refused: " + ex.Message);
            }

            if (!DataRootResolver.FileExists(associations))
                throw new JobRefusedException("The association set does not exist.");
            argv.Add("--associations");
            argv.Add(associations.FullPath);
        }

        argv.AddRange(given);
        if (dryRun)
            argv.Add(CohortCommandLine.DryRunFlag);
        try
        {
            return new Built(CohortCommandLine.Parse(mode, argv.ToArray(), log), input, project);
        }
        catch (CommandLineUsageException ex)
        {
            throw new JobRefusedException(ex.Message);
        }
    }
}
