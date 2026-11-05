using System.Text.Json;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// A cohort conversion's or survey's parameters. The folders are named the way the browser names
/// them, and resolved here: the input as a data root and a root-relative path, the output as a
/// project, one folder under <c>OutputRoot</c>; the key, under anonymization, always goes to
/// <c>KeyRoot/&lt;project&gt;/AnonymizationKey.json</c> (plan section 3.3). Every other option is
/// <see cref="Arguments"/> in Core's own command-line grammar (<c>--rois</c>, <c>--anonymize</c>,
/// <c>--output-spacing</c>, ...), which Core parses and validates: the web adds no grammar of its own.
/// </summary>
/// <param name="AssociationsFile">An ROI association set, relative to <c>StateRoot</c>, or null.</param>
public sealed record CohortJobParameters(
    string InputRootId,
    string InputPath,
    string Project,
    IReadOnlyList<string>? Arguments = null,
    string? AssociationsFile = null);

/// <summary>
/// <c>cohort-convert</c> and <c>cohort-manifest</c>, on the heavy lane: what <c>--cohort-convert</c>
/// and <c>--cohort-manifest</c> do, composed from the same Core calls in the same order (open the
/// key, scan, <c>BuildPlan</c>, <c>ExecuteAsync</c> or <c>ComputeManifestAsync</c>, the document),
/// with the job's token handed to Core and Core's structured progress as the job's. The result is
/// the document the CLI prints, its paths as root tokens. The job holds its project's lock.
/// </summary>
public abstract class CohortJobHandler : IJobHandler
{
    /// <summary>Flags the job sets itself from its parameters, so <see cref="CohortJobParameters.Arguments"/> may not.</summary>
    public static readonly IReadOnlyList<string> ReservedFlags = new[]
    {
        CohortCommandLine.ScanFlag, CohortCommandLine.ManifestFlag, CohortCommandLine.ConvertFlag,
        CohortCommandLine.DryRunFlag, "--input", "--output", "--key-path", "--associations", "--json-out", "--headless",
    };

    private readonly DataRootResolver _resolver;
    private readonly DataRoots _roots;
    private readonly ProjectLocks _locks;
    private readonly RootPathPresenter _presenter;

    protected CohortJobHandler(DataRootResolver resolver, DataRoots roots, ProjectLocks locks, RootPathPresenter presenter)
    {
        _resolver = resolver;
        _roots = roots;
        _locks = locks;
        _presenter = presenter;
    }

    public abstract string Kind { get; }

    public JobLane Lane => JobLane.Heavy;

    protected abstract CohortMode Mode { get; }

    private bool Converts => Mode == CohortMode.Convert;

    public JobDescription Describe(JsonElement parameters)
    {
        var parsed = Build(JobContext.ReadParameters<CohortJobParameters>(parameters), log: null);
        string cli = CommandLineText.Join(CohortCommandLine.Render(parsed.Command, _presenter.Commands));
        string verb = Converts ? "Convert" : "Survey";
        return new JobDescription($"{verb} {parsed.Input.Presented} into project {parsed.Project}", cli, parsed.Project);
    }

    public async Task<JobOutcome> RunAsync(JobContext context)
    {
        var parsed = Build(context.ReadParameters<CohortJobParameters>(), context.Log);
        var log = context.Log;
        var ct = context.CancellationToken;
        if (!DataRootResolver.DirectoryExists(parsed.Input))
            return JobOutcome.Failed($"Input folder not found: {parsed.Input.Presented}");

        var options = parsed.Command.Options;
        AnonymizationService? anon = null;
        if (options.Anonymize)
        {
            try
            {
                anon = AnonymizationKeyLocation.Open(options.OutputRoot, options.KeyPath, options.Salt, out string legacyKeyWarning);
                if (legacyKeyWarning is not null)
                    log.Report("  WARNING: " + legacyKeyWarning);
            }
            catch (ArgumentException ex)
            {
                return JobOutcome.Failed(ex.Message);
            }

            log.Report($"  Anonymization key: {_presenter.Present(anon.KeyFilePath)}");
        }

        log.Report($"  Scanning {parsed.Input.Presented} ...");
        var scan = await new DicomScannerService().ScanFolderAsync(parsed.Input.FullPath, log, ct, context.ScanProgress);
        if (scan.SkippedErrorCount > 0)
            log.Report($"  WARNING: {scan.SkippedErrorCount} file(s) could not be read; see unreadable_samples.");
        ct.ThrowIfCancellationRequested();

        var plan = CohortExportService.BuildPlan(scan, options, anon);
        log.Report($"  Planned {plan.Series.Count} series ({plan.Skipped.Count} skipped, {plan.UnlinkedRtObjects.Count} unlinked RT objects)");
        context.Report(new JobProgress("series", 0, plan.Series.Count, true));

        var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));
        var result = Converts
            ? await service.ExecuteAsync(plan, options, anon, log, ct, context.CohortProgress)
            : await service.ComputeManifestAsync(plan, options, anon, log, ct, context.CohortProgress);

        object document = Converts
            ? CohortConvertDocument.Build(result, _presenter.Documents)
            : CohortManifestDocument.Build(result, _presenter.Documents);
        context.SetResult(CohortDocuments.Serialize(document));

        // The CLI's exit 1 cases end the job as failed; the document above still records the run.
        string? nothingAssociated = CohortExportService.DescribeNothingAssociated(plan, options, result);
        if (nothingAssociated is not null)
            return JobOutcome.Failed(nothingAssociated);
        int total = result.SucceededCount + result.FailedCount;
        if (result.FailedCount > 0 && result.SucceededCount == 0)
            return JobOutcome.Failed($"All {total} series failed.");
        if (result.FailedCount > 0)
            return JobOutcome.WithFailures($"{result.FailedCount} of {total} series failed.");
        return JobOutcome.Succeeded($"{result.SucceededCount} series {(Converts ? "converted" : "surveyed")}.");
    }

    private sealed record Parsed(CohortCommand Command, ResolvedPath Input, string Project);

    /// <summary>The command line the job runs, with server paths, parsed by Core's grammar.</summary>
    private Parsed Build(CohortJobParameters parameters, IProgress<string>? log)
    {
        if (!_resolver.TryResolve(parameters.InputRootId, parameters.InputPath, RootAccess.Read, out var input, out string? refusal))
            throw new JobRefusedException(refusal);
        var project = _locks.ResolveProject(parameters.Project);

        string[] arguments = (parameters.Arguments ?? Array.Empty<string>()).ToArray();
        foreach (string reserved in ReservedFlags)
        {
            if (CommandLineArgs.HasFlag(arguments, reserved))
                throw new JobRefusedException($"{reserved} is set by the job from its folders, not given as an argument.");
        }

        var argv = new List<string> { CohortCommandLine.FlagFor(Mode), "--input", input.FullPath, "--output", project.FullPath };
        if (CommandLineArgs.HasFlag(arguments, "--anonymize"))
        {
            if (_roots.Folder(ServerFolder.Keys) is null)
                throw new JobRefusedException("Anonymized jobs need Web:KeyRoot: the key is never written into the output root.");
            var key = _resolver.ResolveServerPath(ServerFolder.Keys, parameters.Project + "/" + AnonymizationKeyLocation.FileName);
            argv.Add("--key-path");
            argv.Add(key.FullPath);
        }

        if (parameters.AssociationsFile is not null)
        {
            ResolvedPath associations;
            try
            {
                associations = _resolver.ResolveServerPath(ServerFolder.State, parameters.AssociationsFile);
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

        argv.AddRange(arguments);
        try
        {
            return new Parsed(CohortCommandLine.Parse(Mode, argv.ToArray(), log), input, parameters.Project);
        }
        catch (CommandLineUsageException ex)
        {
            throw new JobRefusedException(ex.Message);
        }
    }
}

public sealed class CohortConvertJobHandler : CohortJobHandler
{
    public const string KindName = "cohort-convert";

    public CohortConvertJobHandler(DataRootResolver resolver, DataRoots roots, ProjectLocks locks, RootPathPresenter presenter)
        : base(resolver, roots, locks, presenter)
    {
    }

    public override string Kind => KindName;

    protected override CohortMode Mode => CohortMode.Convert;
}

public sealed class CohortManifestJobHandler : CohortJobHandler
{
    public const string KindName = "cohort-manifest";

    public CohortManifestJobHandler(DataRootResolver resolver, DataRoots roots, ProjectLocks locks, RootPathPresenter presenter)
        : base(resolver, roots, locks, presenter)
    {
    }

    public override string Kind => KindName;

    protected override CohortMode Mode => CohortMode.Manifest;
}
