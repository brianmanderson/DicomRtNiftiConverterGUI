using DicomRtNifti.Web.Shell;
using DicomRtNifti.Web.State;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DicomRtNifti.Web.Features.Export;

/// <summary>
/// Plan section 4.6: every <c>CohortExportOptions</c> field, grouped as the desktop app's Export
/// Options window groups them, prefilled from a preset, validated by Core, with the live
/// <c>--cohort-convert</c> / <c>--cohort-manifest</c> command. It also registers what the forward
/// path's three pages share: the project store, the user's presets, the circuit's
/// <see cref="ExportSession"/> and the command builder.
/// </summary>
public sealed class ExportFeature : IWebFeature
{
    public string Title => "Export setup";

    public NavSection Section => NavSection.Cohort;

    public int Order => 50;

    public Type Page => typeof(ExportPage);

    public string Summary => "Set every export option, prefilled from your preset: selection, what to write, ROIs, spacing, anonymization, metadata.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR [options]", "DicomRtNifti.Cli --help lists every option; the page renders the full command from the options on screen.");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => AddServices(services);

    /// <summary>The services the Curation, Export and Plan pages share; idempotent.</summary>
    public static void AddServices(IServiceCollection services)
    {
        services.TryAddSingleton<ProjectStore>();
        services.TryAddSingleton<UserPreferences>();
        services.TryAddSingleton<CohortJobArgv>();
        services.TryAddSingleton<ExportCommandBuilder>();
        services.TryAddScoped<ExportSession>();
        // Inventory (W4-G) registers the circuit's real selection; until then, the whole folder.
        services.TryAddScoped<IInventorySelection, NoInventorySelection>();
    }
}
