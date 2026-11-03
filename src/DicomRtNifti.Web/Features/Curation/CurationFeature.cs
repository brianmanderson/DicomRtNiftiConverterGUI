using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.Curation;

/// <summary>Plan section 4.5. A placeholder until brief W4-H builds the page.</summary>
public sealed class CurationFeature : IWebFeature
{
    public string Title => "ROI curation";

    public NavSection Section => NavSection.Cohort;

    public int Order => 40;

    public Type Page => typeof(CurationPage);

    public string Summary => "Build the association set that names ROIs canonically, choose which ROIs to export, and see coverage per patient.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-convert --input PATH --output DIR --associations roi_associations.json --only-associated-rois", "The association set downloads as the JSON file --associations reads.");
}
