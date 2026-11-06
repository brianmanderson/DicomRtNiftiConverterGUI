using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.State;

namespace DicomRtNifti.Web.Features.Inventory;

/// <summary>
/// The Inventory's selection as Export setup and Plan review read it (<see cref="IInventorySelection"/>):
/// the image series ticked in the snapshot this circuit is looking at, read live, so a change in the
/// tree shows in the export at once. The Inventory selects series only (a patient's or study's
/// checkbox selects its series), so the patient and structure-set lists stay empty, which Core
/// reads as "every one". No snapshot, or nothing ticked, is the whole scan.
/// </summary>
public sealed class InventoryExportSelection : IInventorySelection
{
    private readonly ScanSession _session;

    public InventoryExportSelection(ScanSession session)
    {
        _session = session;
    }

    public IReadOnlyList<string> PatientIds => Array.Empty<string>();

    public IReadOnlyList<string> SeriesInstanceUids =>
        _session.Current?.Selection.SeriesInstanceUids() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<string> StructureSetUids => Array.Empty<string>();
}
