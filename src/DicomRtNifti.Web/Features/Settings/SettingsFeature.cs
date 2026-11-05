using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Settings;

/// <summary>Plan section 4.13. A placeholder; no brief builds the page yet.</summary>
public sealed class SettingsFeature : IWebFeature
{
    public string Title => "Settings";

    public NavSection Section => NavSection.Server;

    public int Order => 10;

    public Type Page => typeof(SettingsPage);

    public string Summary => "See this server's roots, limits and lanes, and manage your presets and each project's association sets.";

    public CliEquivalent Cli { get; } = new(null, "None: server settings are a file (appsettings.json, or Web__ environment variables).");
}
