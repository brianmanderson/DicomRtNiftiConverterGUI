using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Help;

/// <summary>Plan section 4.14. A placeholder; no brief builds the page yet.</summary>
public sealed class HelpFeature : IWebFeature
{
    public string Title => "Help";

    public NavSection Section => NavSection.Server;

    public int Order => 20;

    public Type Page => typeof(HelpPage);

    public string Summary => "Per-page help and the table of each page's CLI equivalent.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --help", "Prints every mode and its options.");
}
