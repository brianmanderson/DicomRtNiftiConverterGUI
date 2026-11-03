using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Export;

/// <summary>Plan section 4.6. A placeholder until brief W4-H builds the page.</summary>
public sealed class ExportFeature : IWebFeature
{
    public string Title => "Export setup";

    public NavSection Section => NavSection.Cohort;

    public int Order => 50;

    public Type Page => typeof(ExportPage);

    public string Summary => "Set every export option, prefilled from your preset: selection, what to write, ROIs, spacing, anonymization, metadata.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR [options]", "DicomRtNifti.Cli --help lists every option; the page will render the full command from the options on screen.");
}
