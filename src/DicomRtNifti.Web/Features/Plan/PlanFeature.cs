using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Plan;

/// <summary>Plan section 4.7. A placeholder until brief W4-H builds the page.</summary>
public sealed class PlanFeature : IWebFeature
{
    public string Title => "Plan review";

    public NavSection Section => NavSection.Cohort;

    public int Order => 60;

    public Type Page => typeof(PlanPage);

    public string Summary => "Review what a conversion would write, and what it would skip and why, before anything is written.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR --dry-run", "--dry-run prints the plan (dicomrtnifti.cohort-plan/1) and writes nothing.");
}
