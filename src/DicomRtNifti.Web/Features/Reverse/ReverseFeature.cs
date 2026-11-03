using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Reverse;

/// <summary>Plan section 4.10. A placeholder until brief W4-N builds the page.</summary>
public sealed class ReverseFeature : IWebFeature
{
    public string Title => "Reverse";

    public NavSection Section => NavSection.MoreInputs;

    public int Order => 10;

    public Type Page => typeof(ReversePage);

    public string Summary => "Convert NIfTI case folders back to DICOM: image series, RT-STRUCT and RT-DOSE.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --reverse --masks-folder PATH --output PATH", "One case and its masks; brief W4-L adds --reverse-batch with image series and RT-DOSE.");
}
