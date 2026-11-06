using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Shell;
using DicomRtNifti.Web.State;

namespace DicomRtNifti.Web.Features.DataSources;

/// <summary>
/// Plan section 4.2: pick a folder under a data root, scan it as a job, follow its progress, and
/// open the result in the Inventory. Registers the scan state and the scan job that fills it.
/// </summary>
public sealed class DataSourcesFeature : IWebFeature
{
    public string Title => "Data sources";

    public NavSection Section => NavSection.Cohort;

    public int Order => 10;

    public Type Page => typeof(DataSourcesPage);

    public string Summary => "Choose a data root and a folder under it, and scan it recursively for DICOM.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-scan --input PATH --json-out scan.json", "PATH is the folder's server path; the page names the same folder as root:folder.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ScanStateServices.Add(services);
        JobServices.AddHandler<SnapshotScanJobHandler>(services);
    }
}
