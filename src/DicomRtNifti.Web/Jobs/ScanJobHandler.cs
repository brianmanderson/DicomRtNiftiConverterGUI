using System.Text.Json;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Jobs;

/// <summary>A scan's parameters: a folder the browser named, as a root id and a root-relative path.</summary>
public sealed record ScanJobParameters(string RootId, string RelativePath = "");

/// <summary>
/// <c>scan</c>, on the light lane: Core's <c>DicomScannerService.ScanFolderAsync</c> over one
/// folder under a data root, with its file counts as progress, and the inventory Core builds
/// (<c>dicomrtnifti.cohort-scan/1</c>, the bytes <c>--cohort-scan</c> prints for the same folder,
/// the input root shown as its root token) as the result. Header reads only: it needs no native.
/// </summary>
public sealed class ScanJobHandler : IJobHandler
{
    public const string KindName = "scan";

    private readonly DataRootResolver _resolver;
    private readonly RootPathPresenter _presenter;

    public ScanJobHandler(DataRootResolver resolver, RootPathPresenter presenter)
    {
        _resolver = resolver;
        _presenter = presenter;
    }

    public string Kind => KindName;

    public JobLane Lane => JobLane.Light;

    public JobDescription Describe(JsonElement parameters)
    {
        var folder = Resolve(JobContext.ReadParameters<ScanJobParameters>(parameters));
        var options = new CohortExportOptions { InputRoot = folder.FullPath };
        string cli = CommandLineText.Join(CohortCommandLine.Render(CohortMode.Scan, options, _presenter.Commands));
        return new JobDescription("Scan " + folder.Presented, cli);
    }

    public async Task<JobOutcome> RunAsync(JobContext context)
    {
        var folder = Resolve(context.ReadParameters<ScanJobParameters>());
        if (!DataRootResolver.DirectoryExists(folder))
            return JobOutcome.Failed($"Input folder not found: {folder.Presented}");

        context.Log.Report($"  Scanning {folder.Presented} ...");
        var scan = await new DicomScannerService()
            .ScanFolderAsync(folder.FullPath, context.Log, context.CancellationToken, context.ScanProgress);

        context.SetResult(CohortDocuments.Serialize(CohortScanDocument.Build(scan, folder.FullPath, _presenter.Documents)));

        // Image series as the scan document counts them, by Core's rule; RT objects are not series here.
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
