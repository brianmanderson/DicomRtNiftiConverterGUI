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
        /// Image slices left out of <see cref="FilePaths"/> because ImagePositionPatient was
        /// absent or unparseable, each as "file (InstanceNumber n): reason". A slice without a
        /// usable position cannot be placed along the slice axis, so the CLI's series builder and
        /// the scanner both skip it and record it here; <see cref="Services.SeriesGeometryProbe"/>
        /// names it in a warning instead of misreading the hole as non-uniform spacing. Empty on a
        /// clean series.
        /// </summary>
        public List<string> UnpositionedSlices { get; set; } = new List<string>();

        /// <summary>
        /// PixelSpacing (0028,0030) in mm from the series' first slice, in DICOM's order: the
        /// spacing between rows, then between columns — that is [y, x], not [x, y]. Null when the
        /// tag is absent (as on RTSTRUCT).
        /// </summary>
        public double[] PixelSpacing { get; set; }

        /// <summary>
        /// SliceThickness (0018,0050) in mm from the first slice, or null when absent. Used only
        /// as the through-plane fallback when a series has too few slices to derive spacing.
        /// </summary>
        public double? SliceThickness { get; set; }

        /// <summary>
        /// ImageOrientationPatient (0020,0037) of the first file that carries it: the row
        /// direction cosines, then the column ones. Null when no file carries a usable value.
        /// </summary>
        public double[] ImageOrientationPatient { get; set; }

        /// <summary>
        /// True when some file's ImageOrientationPatient differs from <see cref="ImageOrientationPatient"/>
        /// by more than 1e-4 in a component: the files are not one stack of parallel slices.
        /// </summary>
        public bool OrientationVaries { get; set; }

        /// <summary>Rows (0028,0010) of the first file that carries it, or null.</summary>
        public int? Rows { get; set; }

        /// <summary>Columns (0028,0011) of the first file that carries it, or null.</summary>
        public int? Columns { get; set; }

        /// <summary>
        /// RescaleSlope (0028,1053) of the first file that carries a rescale (either tag), or
        /// null when that file has no slope. See <see cref="RescaleVaries"/>.
        /// </summary>
        public double? RescaleSlope { get; set; }

        /// <summary>RescaleIntercept (0028,1052), from the same file as <see cref="RescaleSlope"/>.</summary>
        public double? RescaleIntercept { get; set; }

        /// <summary>True when some file of the series carries RescaleSlope or RescaleIntercept.</summary>
        public bool HasRescale { get; set; }

        /// <summary>
        /// True when the files disagree on the rescale, as PET series that scale each slice do:
        /// <see cref="RescaleSlope"/> and <see cref="RescaleIntercept"/> are then one file's values,
        /// not the series'.
        /// </summary>
        public bool RescaleVaries { get; set; }

        /// <summary>NumberOfFrames (0028,0008) of the first file that carries it, or null (single-frame objects).</summary>
        public int? NumberOfFrames { get; set; }

        /// <summary>
        /// How many files carried each Modality value, in ordinal order of the value. The scan
        /// sets <see cref="Modality"/> to the majority; any other entry is an instance it
        /// outvoted (a stray localizer or secondary capture). Empty when no file had a Modality.
        /// </summary>
        public Dictionary<string, int> ModalityCounts { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// RTSTRUCT only: every frame of reference the structure set names — the top-level tag,
        /// ReferencedFrameOfReferenceSequence and each ROI's ReferencedFrameOfReferenceUID, in that
        /// order, without duplicates. <see cref="FrameOfReferenceUID"/> is the first of them.
        /// </summary>
        public List<string> FrameOfReferenceUIDs { get; set; } = new List<string>();

        /// <summary>
        /// RTSTRUCT only: names StructureSetROISequence lists more than once within one structure
        /// set, each once, in order of first repeat. <see cref="RoiNames"/> keeps a single copy;
        /// an export keeps the name for the lowest ROINumber and suffixes the others with theirs.
        /// </summary>
        public List<string> DuplicateRoiNames { get; set; } = new List<string>();

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
        /// A study whose RT objects all resolved via <see cref="RtLinkMatchRule.LargestSeriesFallback"/>
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
