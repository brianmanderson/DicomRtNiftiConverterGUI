using System.Text.Json;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;

namespace DicomRtNifti.Web.Features.Plan;

/// <summary>
/// A dry run's parameters: a cohort job's (<see cref="CohortJobParameters"/>) and whether it plans
/// a survey (<c>--cohort-manifest</c>) rather than a conversion.
/// </summary>
public sealed record PlanJobParameters(
    string InputRootId,
    string InputPath,
    string Project,
    IReadOnlyList<string>? Arguments = null,
    string? AssociationsFile = null,
    bool Survey = false)
{
    public static PlanJobParameters For(CohortJobParameters job, bool survey) =>
        new(job.InputRootId, job.InputPath, job.Project, job.Arguments, job.AssociationsFile, survey);

    public CohortMode Mode => Survey ? CohortMode.Manifest : CohortMode.Convert;
}

/// <summary>
/// <c>cohort-plan</c>, on the light lane (plan section 4.7): what <c>--cohort-convert --dry-run</c>
/// and <c>--cohort-manifest --dry-run</c> do, composed from the same Core calls in the same order
/// (the CLI's <c>CohortRunner.RunPlan</c>): open the key read-only, scan, <c>BuildPlan</c>, and the
/// plan document, <c>dicomrtnifti.cohort-plan/1</c>, with its paths as root tokens. It writes
/// nothing (no output folder, no manifest, no key: the key is opened by
/// <see cref="ReadOnlyAnonymizationKey"/>), so it holds no project lock. Header reads only.
/// </summary>
public sealed class PlanJobHandler : IJobHandler
{
    public const string KindName = "cohort-plan";

    private readonly CohortJobArgv _argv;
    private readonly RootPathPresenter _presenter;

    public PlanJobHandler(CohortJobArgv argv, RootPathPresenter presenter)
    {
        _argv = argv;
        _presenter = presenter;
    }

    public string Kind => KindName;

    public JobLane Lane => JobLane.Light;

    public JobDescription Describe(JsonElement parameters)
    {
        var request = JobContext.ReadParameters<PlanJobParameters>(parameters);
        var built = Build(request, log: null);
        string cli = CommandLineText.Join(ExportCommandBuilder.MaskSalt(CohortCommandLine.Render(built.Command, _presenter.Commands)));
        string verb = request.Survey ? "survey" : "conversion";
        return new JobDescription($"Plan the {verb} of {built.Input.Presented} into project {request.Project}", cli);
    }

    public async Task<JobOutcome> RunAsync(JobContext context)
    {
        var request = context.ReadParameters<PlanJobParameters>();
        var log = context.Log;
        var built = Build(request, log);
        var options = built.Command.Options;
        if (!DataRootResolver.DirectoryExists(built.Input))
            return JobOutcome.Failed($"Input folder not found: {built.Input.Presented}");

        AnonymizationService? anon = null;
        string? keyFile = null;
        if (options.Anonymize)
        {
            try
            {
                anon = ReadOnlyAnonymizationKey.OpenForExport(
                    options.OutputRoot, options.KeyPath, options.Salt, out keyFile, out string legacyKeyWarning);
                if (legacyKeyWarning is not null)
                    log.Report("  WARNING: " + legacyKeyWarning);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                return JobOutcome.Failed(ex.Message);
            }

            log.Report($"  Anonymization key: {_presenter.Present(keyFile)} (read only)");
        }

        log.Report($"  Scanning {built.Input.Presented} ...");
        var scan = await new DicomScannerService().ScanFolderAsync(built.Input.FullPath, log, context.CancellationToken, context.ScanProgress);
        if (scan.SkippedErrorCount > 0)
            log.Report($"  WARNING: {scan.SkippedErrorCount} file(s) could not be read; see unreadable_samples.");
        context.CancellationToken.ThrowIfCancellationRequested();

        var plan = CohortExportService.BuildPlan(scan, options, anon);
        log.Report($"  Planned {plan.Series.Count} series ({plan.Skipped.Count} skipped, {plan.UnlinkedRtObjects.Count} unlinked RT objects)");
        log.Report("  Dry run: nothing written.");

        context.SetResult(CohortDocuments.Serialize(CohortPlanDocument.Build(
            built.Command.Mode, plan, options, anon, keyFile, _presenter.Documents)));
        return JobOutcome.Succeeded($"{plan.Series.Count} series planned, {plan.Skipped.Count} skipped.");
    }

    private CohortJobArgv.Built Build(PlanJobParameters request, IProgress<string>? log) =>
        _argv.Build(request.Mode, request.InputRootId, request.InputPath, request.Project,
            request.Arguments, request.AssociationsFile, dryRun: true, log);
}
