using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Inventory;

/// <summary>Plan section 4.3. A placeholder until brief W4-G builds the page.</summary>
public sealed class InventoryFeature : IWebFeature
{
    public string Title => "Inventory";

    public NavSection Section => NavSection.Cohort;

    public int Order => 20;

    public Type Page => typeof(InventoryPage);

    public string Summary => "Browse the scanned Patient, Study and Series tree with its geometry and link badges, and choose what to export.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-scan --input PATH", "The scan document lists every series with its links and geometry; --patients ID,... selects patients on the cohort modes.");
}
