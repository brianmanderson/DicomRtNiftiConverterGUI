using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Watch;

/// <summary>Plan section 4.12. A placeholder until brief W4-N builds the page.</summary>
public sealed class WatchFeature : IWebFeature
{
    public string Title => "Watch folders";

    public NavSection Section => NavSection.MoreInputs;

    public int Order => 30;

    public Type Page => typeof(WatchPage);

    public string Summary => "Watch a read-write root and convert each case once it has stopped changing.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --watch --input ROOT --interval SECONDS", "Brief W4-L adds --watch; today the desktop GUI's Run Server does this.", Planned: true);
}
