using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Shell;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DicomRtNifti.Web.Features.Curation;

/// <summary>
/// Plan section 4.5: the project's association sets (edit, import, download as the JSON
/// <c>--associations</c> reads), the cohort's ROI names, clusters and coverage from Core's
/// <c>RoiNamingAnalysis</c>, and the ROI selection the export takes. Registers the
/// <c>cohort-analyze</c> job kind that scans for the names (<see cref="RoiAnalysisJobHandler"/>).
/// </summary>
public sealed class CurationFeature : IWebFeature
{
    public string Title => "ROI curation";

    public NavSection Section => NavSection.Cohort;

    public int Order => 40;

    public Type Page => typeof(CurationPage);

    public string Summary => "Build the association set that names ROIs canonically, choose which ROIs to export, and see coverage per patient.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR --associations roi_associations.json --only-associated-rois", "The association set downloads as the JSON file --associations reads.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ExportFeature.AddServices(services);
        services.TryAddSingleton<RoiScanCache>();
        // Inventory's snapshot store (W4-G) registers its own ICohortScanSource; until then, the stand-in.
        services.TryAddSingleton<ICohortScanSource>(sp => sp.GetRequiredService<RoiScanCache>());
        JobServices.AddHandler<RoiAnalysisJobHandler>(services);
    }
}
