using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Jobs;

/// <summary>Plan section 4.8. A placeholder until brief W4-E builds the page.</summary>
public sealed class JobsFeature : IWebFeature
{
    public string Title => "Jobs";

    public NavSection Section => NavSection.Cohort;

    public int Order => 70;

    public Type Page => typeof(JobsPage);

    public string Summary => "Follow queued and running scans, conversions and analyses: progress, log, cancel and history.";

    public CliEquivalent Cli { get; } = new(null, "Each job records the CLI command it is equivalent to.");
}
