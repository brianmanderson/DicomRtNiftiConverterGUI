using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
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
        /// Patient-coordinate z of every slice, collected during the scan. Unsorted until the
        /// scanner orders the hierarchy. Feeds slice-spacing and uniformity reporting without a
        /// second pass over the files.
        /// </summary>
        public List<double> SlicePositions { get; set; } = new List<double>();

        /// <summary>
        /// In-plane spacing [row, column] in mm from the series' first slice, or null when the
        /// tag is absent (as on RTSTRUCT).
        /// </summary>
        public double[] PixelSpacing { get; set; }

        /// <summary>
        /// SliceThickness (0018,0050) in mm from the first slice, or null when absent. Used only
        /// as the through-plane fallback when a series has too few slices to derive spacing.
        /// </summary>
        public double? SliceThickness { get; set; }

        /// <summary>
        /// The first RTSTRUCT series linked to this image series (null if none found).
        /// Retained for backward compatibility; prefer <see cref="LinkedRtStructs"/>, which
        /// captures every linked structure set.
        /// </summary>
        public DicomSeriesGroup LinkedRtStruct { get; set; }

        /// <summary>
        /// All RTSTRUCT series linked to this image series. A study can contain more than one
        /// structure set (a planning-CT set plus per-fraction CBCT sets, or successive
        /// re-contourings), so a single reference silently dropped all but the last. This list
        /// captures every linked structure set.
        /// </summary>
        public List<DicomSeriesGroup> LinkedRtStructs { get; set; } = new List<DicomSeriesGroup>();

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

        /// <summary>
        /// Set on an RTSTRUCT/RTDOSE series to record how it was matched to its image series.
        /// A study whose RT objects all resolved via <see cref="RtLinkMatchRule.FirstSeriesFallback"/>
        /// is a QC signal, not a success: the link is a guess. Left at
        /// <see cref="RtLinkMatchRule.None"/> on image series and on unlinked RT objects.
        /// </summary>
        public RtLinkMatchRule LinkMatchRule { get; set; } = RtLinkMatchRule.None;
    }

    /// <summary>
    /// Which rule matched an RT object to its image series, in descending order of confidence.
    /// </summary>
    public enum RtLinkMatchRule
    {
        /// <summary>Not linked, or not an RT object.</summary>
        None = 0,

        /// <summary>Matched on the RT object's referenced image SeriesInstanceUID. Authoritative.</summary>
        ReferencedSeriesUid = 1,

        /// <summary>
        /// Matched on FrameOfReferenceUID, resolving ties toward the series with the most
        /// instances. Reliable only when one image series per frame of reference exists — CBCTs
        /// resampled onto the planning CT's grid share its frame of reference, so this rule
        /// cannot distinguish them and falls back to "the fullest series wins".
        /// </summary>
        FrameOfReferenceUid = 2,

        /// <summary>
        /// Nothing matched, so the largest image series in the study was assumed. A guess;
        /// report it.
        /// </summary>
        LargestSeriesFallback = 3,
    }
}
