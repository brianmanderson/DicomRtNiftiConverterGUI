using System.Text.Json;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;

namespace DicomRtNifti.Web.Features.Curation;

/// <summary>The cohort folder ROI curation reads, and the association set to resolve its names against.</summary>
public sealed record RoiAnalysisJobParameters(string InputRootId, string InputPath = "", string? AssociationsFile = null);

/// <summary>
/// <c>cohort-analyze</c>, on the light lane: what <c>--cohort-analyze --tier header</c> does, from
/// the same Core calls (scan, <see cref="CohortAnalysisService.Analyze"/>, the document
/// <c>dicomrtnifti.cohort-analysis/1</c>, its input root as a root token). ROI curation reads the
/// document's <c>roi_naming</c> section, which is the coverage the plan names (section 4.5). The
/// scan itself is kept, by folder, in <see cref="RoiScanCache"/> (the stand-in
/// <see cref="ICohortScanSource"/> until Inventory's snapshot store serves scans) so the page can
/// re-count names, clusters and coverage against the association set being edited without walking
/// the tree again: that re-count is Core's <see cref="RoiNamingAnalysis"/> over the same scan,
/// nothing of the web's.
/// Names are shown as they are: this kind does not anonymize.
/// </summary>
public sealed class RoiAnalysisJobHandler : IJobHandler
{
    public const string KindName = "cohort-analyze";

    private readonly CohortJobArgv _argv;
    private readonly RootPathPresenter _presenter;
    private readonly RoiScanCache _cache;

    public RoiAnalysisJobHandler(CohortJobArgv argv, RootPathPresenter presenter, RoiScanCache cache)
    {
        _argv = argv;
        _presenter = presenter;
        _cache = cache;
    }

    public string Kind => KindName;

    public JobLane Lane => JobLane.Light;

    public JobDescription Describe(JsonElement parameters)
    {
        var built = Build(JobContext.ReadParameters<RoiAnalysisJobParameters>(parameters), log: null);
        string cli = CommandLineText.Join(CohortCommandLine.Render(built.Command, _presenter.Commands));
        return new JobDescription("Analyze the ROI names of " + built.Input.Presented, cli);
    }

    public async Task<JobOutcome> RunAsync(JobContext context)
    {
        var built = Build(context.ReadParameters<RoiAnalysisJobParameters>(), context.Log);
        if (!DataRootResolver.DirectoryExists(built.Input))
            return JobOutcome.Failed($"Input folder not found: {built.Input.Presented}");

        context.Log.Report($"  Scanning {built.Input.Presented} ...");
        var scan = await new DicomScannerService()
            .ScanFolderAsync(built.Input.FullPath, context.Log, context.CancellationToken, context.ScanProgress);
        if (scan.SkippedErrorCount > 0)
            context.Log.Report($"  WARNING: {scan.SkippedErrorCount} file(s) could not be read; see unreadable_samples.");
        context.CancellationToken.ThrowIfCancellationRequested();

        var command = built.Command;
        var analysis = CohortAnalysisService.Analyze(scan, new CohortAnalysisOptions
        {
            Tier = command.Tier,
            InputRoot = built.Input.FullPath,
            Associations = command.Options.Associations,
            AssociationsName = command.AssociationsPath == null ? null : Path.GetFileName(command.AssociationsPath),
        });
        context.SetResult(CohortDocuments.Serialize(CohortAnalysisDocument.Build(analysis, _presenter.Documents)));
        _cache.Put(built.Input, scan);

        var naming = analysis.RoiNaming;
        return JobOutcome.Succeeded(
            $"{naming.Names.Count} ROI name(s) in {naming.Coverage.PatientsWithRois} patient(s); {naming.Clusters.Count} cluster(s).");
    }

    private CohortJobArgv.Built Build(RoiAnalysisJobParameters request, IProgress<string>? log) =>
        _argv.Build(CohortMode.Analyze, request.InputRootId, request.InputPath, project: null,
            arguments: null, request.AssociationsFile, dryRun: false, log);
}
