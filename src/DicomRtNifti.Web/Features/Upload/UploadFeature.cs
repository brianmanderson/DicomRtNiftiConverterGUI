using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Upload;

/// <summary>Plan section 4.11. A placeholder until brief W4-N builds the page.</summary>
public sealed class UploadFeature : IWebFeature
{
    public string Title => "Upload";

    public NavSection Section => NavSection.MoreInputs;

    public int Order => 20;

    public Type Page => typeof(UploadPage);

    public string Summary => "Upload one small case from your own machine for a one-off conversion; never a cohort.";

    public CliEquivalent Cli { get; } = new(null, "None: the CLI reads local files.");
}
