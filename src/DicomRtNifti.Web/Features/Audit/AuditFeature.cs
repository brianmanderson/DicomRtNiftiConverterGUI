using DicomRtNifti.Web.Audit;
using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Audit;

/// <summary>Plan section 4.15: the audit log, newest first, for administrators.</summary>
public sealed class AuditFeature : IWebFeature
{
    public string Title => "Audit";

    public NavSection Section => NavSection.Server;

    public int Order => 30;

    public Type Page => typeof(AuditPage);

    public string Summary => "Who ran what, when, on which root and with which options, and every download. For administrators.";

    public CliEquivalent Cli { get; } = new(null, "None: the web server writes the audit log.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => AuditServices.Add(services);
}
