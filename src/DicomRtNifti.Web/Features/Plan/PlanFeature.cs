using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Plan;

/// <summary>
/// Plan section 4.7: the dry run's plan document (<c>dicomrtnifti.cohort-plan/1</c>) for the
/// export on Export setup, then Run conversion or Run survey on the heavy lane. Registers the
/// <c>cohort-plan</c> job kind (<see cref="PlanJobHandler"/>).
/// </summary>
public sealed class PlanFeature : IWebFeature
{
    public string Title => "Plan review";

    public NavSection Section => NavSection.Cohort;

    public int Order => 60;

    public Type Page => typeof(PlanPage);

    public string Summary => "Review what a conversion would write, and what it would skip and why, before anything is written.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR --dry-run", "--dry-run prints the plan (dicomrtnifti.cohort-plan/1) and writes nothing.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ExportFeature.AddServices(services);
        JobServices.AddHandler<PlanJobHandler>(services);
    }
}
