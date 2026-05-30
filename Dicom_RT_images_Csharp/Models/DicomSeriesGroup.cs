using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Groups all DICOM files within a single series, with links to associated RT data.
    /// </summary>
    public class DicomSeriesGroup
    {
        /// <summary>
        /// DICOM tag (0020,000E).
        /// </summary>
        public string SeriesInstanceUID { get; set; } = "";

        /// <summary>
        /// DICOM tag (0008,103E).
        /// </summary>
        public string SeriesDescription { get; set; } = "";

        /// <summary>
        /// DICOM tag (0008,0060) — e.g. CT, MR, RTSTRUCT, RTDOSE, RTPLAN.
        /// </summary>
        public string Modality { get; set; } = "";

        /// <summary>
        /// DICOM tag (0008,0021).
        /// </summary>
        public string SeriesDate { get; set; } = "";

        /// <summary>
        /// DICOM tag (0020,0052).
        /// </summary>
        public string FrameOfReferenceUID { get; set; } = "";

        /// <summary>
        /// Full file paths of all DICOM files belonging to this series.
        /// </summary>
        public List<string> FilePaths { get; set; } = new List<string>();

        /// <summary>
        /// ROI names parsed from StructureSetROISequence (only populated for RTSTRUCT series).
        /// </summary>
        public List<string> RoiNames { get; set; } = new List<string>();

        /// <summary>
        /// The RTSTRUCT series linked to this image series (null if none found).
        /// </summary>
        public DicomSeriesGroup LinkedRtStruct { get; set; }

        /// <summary>
        /// The first RTDOSE series linked to this image series (null if none found).
        /// Retained for backward compatibility; prefer <see cref="LinkedRtDoses"/>, which
        /// captures every linked dose.
        /// </summary>
        public DicomSeriesGroup LinkedRtDose { get; set; }

        /// <summary>
        /// All RTDOSE series linked to this image series. A study can contain more than one
        /// dose (per-beam, plan-sum, or multiple plans), so a single reference silently
        /// dropped all but the last. This list captures every linked dose.
        /// </summary>
        public List<DicomSeriesGroup> LinkedRtDoses { get; set; } = new List<DicomSeriesGroup>();

        /// <summary>
        /// The referenced image SeriesInstanceUID extracted from RTSTRUCT/RTDOSE (used during linking).
        /// </summary>
        public string ReferencedSeriesUID { get; set; } = "";
    }
}
