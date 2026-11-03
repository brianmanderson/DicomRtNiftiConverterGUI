using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Results;

/// <summary>Plan section 4.9. A placeholder until brief W4-M builds the page.</summary>
public sealed class ResultsFeature : IWebFeature
{
    public string Title => "Results";

    public NavSection Section => NavSection.Cohort;

    public int Order => 80;

    public Type Page => typeof(ResultsPage);

    public string Summary => "Inspect a finished output project: its manifest, its per-series outputs and a post-conversion check.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-verify --output PROJECT", "Brief W4-M adds --cohort-verify; until then the project's export_manifest.csv and series folders are the record.", Planned: true);
}
