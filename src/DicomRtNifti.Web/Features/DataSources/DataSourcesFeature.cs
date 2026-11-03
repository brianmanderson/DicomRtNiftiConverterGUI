using DicomRtNifti.Web.Shell;

namespace DicomRtNifti.Web.Features.DataSources;

/// <summary>Plan section 4.2. A placeholder until brief W4-G builds the page.</summary>
public sealed class DataSourcesFeature : IWebFeature
{
    public string Title => "Data sources";

    public NavSection Section => NavSection.Cohort;

    public int Order => 10;

    public Type Page => typeof(DataSourcesPage);

    public string Summary => "Choose a data root and a folder under it, and scan it recursively for DICOM.";

    public CliEquivalent Cli { get; } = new("DicomRtNifti.Cli --cohort-scan --input PATH --json-out scan.json", "PATH is the folder's server path; the page names the same folder as root:folder.");
}
