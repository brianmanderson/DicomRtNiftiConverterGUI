using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Analysis;

/// <summary>Plan section 4.4. A placeholder until brief W4-J builds the page.</summary>
public sealed class AnalysisFeature : IWebFeature
{
    public string Title => "Analysis";

    public NavSection Section => NavSection.Cohort;

    public int Order => 30;

    public Type Page => typeof(AnalysisPage);

    public string Summary => "See what an archive holds before and after conversion: linkage, geometry, ROI naming and coverage, dose, flags.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-analyze --input PATH --tier header", "Brief W4-F adds --cohort-analyze.", Planned: true);
}
