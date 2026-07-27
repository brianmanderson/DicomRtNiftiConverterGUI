using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// What a cohort export intends to do, resolved before any file is written or read beyond
    /// the scan. Planning is deliberately separated from execution so the selection and naming
    /// rules can be tested without SimpleITK, and so a caller can inspect (or print) the plan
    /// before committing to a long conversion.
    /// </summary>
    public class CohortExportPlan
    {
        /// <summary>Series that will be exported, in scan order.</summary>
        public List<PlannedSeries> Series { get; set; } = new List<PlannedSeries>();

        /// <summary>
        /// Image series that were scanned but excluded, each with the reason. Worth surfacing:
        /// "40 patients scanned, 8 series skipped for no linked RTSTRUCT" is the difference
        /// between a filtered cohort and a broken one.
        /// </summary>
        public List<SkippedSeries> Skipped { get; set; } = new List<SkippedSeries>();

        /// <summary>
        /// RT objects that never linked to any image series, so nothing will consume them.
        /// </summary>
        public List<SkippedSeries> UnlinkedRtObjects { get; set; } = new List<SkippedSeries>();
    }

    /// <summary>One image series and everything that will be written alongside it.</summary>
    public class PlannedSeries
    {
        /// <summary>The image series itself.</summary>
        public DicomSeriesGroup Image { get; set; }

        /// <summary>The structure set chosen for this series, or null when it has none.</summary>
        public DicomSeriesGroup RtStruct { get; set; }

        /// <summary>Every dose linked to this series.</summary>
        public List<DicomSeriesGroup> RtDoses { get; set; } = new List<DicomSeriesGroup>();

        /// <summary>Source PatientID, before anonymization.</summary>
        public string PatientId { get; set; } = "";

        /// <summary>Source StudyInstanceUID, before anonymization.</summary>
        public string StudyUid { get; set; } = "";

        /// <summary>Source SeriesInstanceUID, before anonymization.</summary>
        public string SeriesUid { get; set; } = "";

        /// <summary>
        /// PatientID as it will appear in output paths and the manifest — the hash when
        /// anonymizing, otherwise the source value.
        /// </summary>
        public string ExportPatientId { get; set; } = "";

        /// <summary>StudyInstanceUID as it will appear in output, hashed when anonymizing.</summary>
        public string ExportStudyUid { get; set; } = "";

        /// <summary>SeriesInstanceUID as it will appear in output, hashed when anonymizing.</summary>
        public string ExportSeriesUid { get; set; } = "";

        /// <summary>
        /// Output directory for this series, relative to the cohort root and using forward
        /// slashes, so it can be embedded in JSON and rejoined on any platform.
        /// </summary>
        public string RelativeOutputDir { get; set; } = "";
    }

    /// <summary>A series left out of the export, and why.</summary>
    public class SkippedSeries
    {
        public string PatientId { get; set; } = "";
        public string SeriesInstanceUid { get; set; } = "";
        public string Modality { get; set; } = "";
        public string SeriesDescription { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
