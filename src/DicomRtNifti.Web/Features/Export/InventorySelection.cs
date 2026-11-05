namespace DicomRtNifti.Web.Features.Export;

/// <summary>
/// The patients, image series and structure sets picked in Inventory (plan section 4.3), as the
/// export reads them: Core's own selection lists, <c>CohortExportOptions.PatientIds</c>,
/// <c>SeriesInstanceUids</c> and <c>StructureSetUids</c>, source identifiers, matched by Core.
/// Empty lists select everything, as on the command line.
///
/// The Inventory page (brief W4-G) owns the selection and registers its own implementation, per
/// circuit; until it does, <see cref="NoInventorySelection"/> stands in and the export takes the
/// whole input folder. Export setup reads only this interface, so neither page edits the other's files.
/// </summary>
public interface IInventorySelection
{
    IReadOnlyList<string> PatientIds { get; }

    IReadOnlyList<string> SeriesInstanceUids { get; }

    IReadOnlyList<string> StructureSetUids { get; }
}

/// <summary>No selection: every series of the input folder, as the CLI without <c>--patients</c> or <c>--series</c>.</summary>
public sealed class NoInventorySelection : IInventorySelection
{
    public IReadOnlyList<string> PatientIds => Array.Empty<string>();

    public IReadOnlyList<string> SeriesInstanceUids => Array.Empty<string>();

    public IReadOnlyList<string> StructureSetUids => Array.Empty<string>();
}
