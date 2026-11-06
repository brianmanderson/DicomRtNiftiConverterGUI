using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Web.Features.Export;

/// <summary>
/// The forward path's choices in one circuit (plan section 4, page flow: ROI curation, Export
/// setup, Plan review): which folder, which project, which association set, and the export
/// toggles, bound straight to Core's <see cref="CohortExportOptions"/>. Scoped, so it lives as
/// long as the browser tab's circuit and is never in a URL or the browser's storage. The folders,
/// the associations, the selection and the salt in <see cref="Options"/> are not used: the
/// command builder sets them from the fields here, the project and the server
/// (<see cref="ExportCommandBuilder"/>).
/// </summary>
public sealed class ExportSession
{
    /// <summary>The data root holding the cohort.</summary>
    public string? InputRootId { get; set; }

    /// <summary>The cohort folder, relative to its root ('/' separators; empty for the root itself).</summary>
    public string InputPath { get; set; } = "";

    /// <summary>The output project: one folder under <c>OutputRoot</c>.</summary>
    public string? Project { get; set; }

    /// <summary>The project's association set the export uses, or null for none.</summary>
    public string? AssociationSet { get; set; }

    /// <summary>The export toggles, from the user's preset and the form.</summary>
    public CohortExportOptions Options { get; set; } = new();

    /// <summary>What Plan review plans: a conversion or a survey (manifest only).</summary>
    public CohortMode PlanMode { get; set; } = CohortMode.Convert;

    /// <summary>The last ROI-name analysis this circuit ran (ROI curation).</summary>
    public string? AnalysisJobId { get; set; }

    /// <summary>The last dry run this circuit ran (Plan review).</summary>
    public string? PlanJobId { get; set; }

    /// <summary>Raised when another page of the flow changed the session.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
