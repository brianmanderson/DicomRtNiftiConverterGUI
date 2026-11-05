using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Jobs;

/// <summary>
/// Plan section 4.8: the queue, the running job's progress and log, the history, cancel and
/// retry. It also registers the job service itself (<see cref="JobServices"/>), which every page
/// that does work submits to.
/// </summary>
public sealed class JobsFeature : IWebFeature
{
    public string Title => "Jobs";

    public NavSection Section => NavSection.Cohort;

    public int Order => 70;

    public Type Page => typeof(JobsPage);

    public string Summary => "Follow queued and running scans, conversions and analyses: progress, log, cancel and history.";

    public CliEquivalent Cli { get; } = new(null, "Each job records the CLI command it is equivalent to.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => JobServices.Add(services);
}
