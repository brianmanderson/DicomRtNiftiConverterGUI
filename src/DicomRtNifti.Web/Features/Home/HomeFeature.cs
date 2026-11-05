using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Home;

/// <summary>Plan section 4.1: is this server able to work?</summary>
public sealed class HomeFeature : IWebFeature
{
    public string Title => "Home";

    public NavSection Section => NavSection.Start;

    public int Order => 0;

    public Type Page => typeof(HomePage);

    public string Summary => "Whether this server can work: its version, the SimpleITK native, and the folders it is configured with.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --version", "Prints the same version and runs the same native probe.");
}
