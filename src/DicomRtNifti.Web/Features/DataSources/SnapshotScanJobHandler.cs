using System.Text.Json;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;

namespace DicomRtNifti.Web.Features.DataSources;

/// <summary>
/// <c>inventory-scan</c>, the Data sources page's scan: the built-in <c>scan</c> job's work
/// (<see cref="ScanJobHandler"/>: Core's <c>ScanFolderAsync</c>, file counts as progress, the
/// <c>--cohort-scan</c> document as the result, byte for byte), and then the scanned tree kept as
/// a snapshot under the job's id, which the Inventory and every later page read. The built-in
/// kind returns only the document, and a document cannot be planned from: <c>BuildPlan</c> needs
/// the tree itself. Its parameters are the built-in kind's, so a job reads the same either way.
/// </summary>
public sealed class SnapshotScanJobHandler : IJobHandler
{
    public const string KindName = "inventory-scan";

    private readonly DataRootResolver _resolver;
    private readonly RootPathPresenter _presenter;
    private readonly ScanSnapshotStore _snapshots;

    public SnapshotScanJobHandler(DataRootResolver resolver, RootPathPresenter presenter, ScanSnapshotStore snapshots)
    {
        _resolver = resolver;
        _presenter = presenter;
        _snapshots = snapshots;
    }

    public string Kind => KindName;

    public JobLane Lane => JobLane.Light;

    public JobDescription Describe(JsonElement parameters)
    {
        var folder = Resolve(JobContext.ReadParameters<ScanJobParameters>(parameters));
        return new JobDescription("Scan " + folder.Presented, CliFor(folder, _presenter.Commands));
    }

    /// <summary>The scan's command line, its folder shown by <paramref name="presenter"/>.</summary>
    public static string CliFor(ResolvedPath folder, PathPresenter presenter) =>
        CommandLineText.Join(CohortCommandLine.Render(
            CohortMode.Scan, new CohortExportOptions { InputRoot = folder.FullPath }, presenter));

    public async Task<JobOutcome> RunAsync(JobContext context)
    {
        var folder = Resolve(context.ReadParameters<ScanJobParameters>());
        if (!DataRootResolver.DirectoryExists(folder))
            return JobOutcome.Failed($"Input folder not found: {folder.Presented}");

        context.Log.Report($"  Scanning {folder.Presented} ...");
        var scan = await new DicomScannerService()
            .ScanFolderAsync(folder.FullPath, context.Log, context.CancellationToken, context.ScanProgress);

        context.SetResult(CohortDocuments.Serialize(CohortScanDocument.Build(scan, folder.FullPath, _presenter.Documents)));
        _snapshots.Save(context.JobId, folder, scan, DateTimeOffset.UtcNow);

        int series = scan.Patients.Sum(p => p.Studies.Sum(s => s.Series.Count(x => CohortDocuments.IsImageModality(x.Modality))));
        string summary = $"{scan.TotalFileCount} file(s): {scan.Patients.Count} patient(s), {series} image series";
        if (scan.SkippedErrorCount > 0)
        {
            context.Log.Report($"  WARNING: {scan.SkippedErrorCount} file(s) could not be read; see unreadable_samples.");
            return JobOutcome.Succeeded($"{summary}; {scan.SkippedErrorCount} file(s) could not be read.");
        }

        return JobOutcome.Succeeded(summary + ".");
    }

    private ResolvedPath Resolve(ScanJobParameters parameters)
    {
        if (!_resolver.TryResolve(parameters.RootId, parameters.RelativePath, RootAccess.Read, out var folder, out string? refusal))
            throw new JobRefusedException(refusal);
        return folder;
    }
}
