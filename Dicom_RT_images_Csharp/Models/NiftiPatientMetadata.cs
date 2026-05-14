using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Patient/study/frame-of-reference metadata used when converting NIfTI inputs
    /// (image.nii.gz / masks/*.nii.gz / doses/*.nii.gz) into DICOM without a pre-existing
    /// reference image series. Persisted as <c>&lt;dicomFolder&gt;/metadata.json</c> so that
    /// the image, mask, and dose conversion passes share one StudyInstanceUID and one
    /// FrameOfReferenceUID, and so that re-running on the same folder is idempotent.
    /// </summary>
    public class NiftiPatientMetadata
    {
        // ---------- Patient ----------

        public string PatientId { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string PatientBirthDate { get; set; } = "";
        public string PatientSex { get; set; } = "";

        // ---------- Study ----------

        public string StudyInstanceUid { get; set; } = "";
        public string StudyDate { get; set; } = "";
        public string StudyTime { get; set; } = "";
        public string StudyId { get; set; } = "";
        public string AccessionNumber { get; set; } = "";
        public string ReferringPhysicianName { get; set; } = "";

        // ---------- Frame of reference (shared by image + RT-STRUCT + RT-DOSE) ----------

        public string FrameOfReferenceUid { get; set; } = "";

        // ---------- Image series (populated by NiftiImageWriterService after a successful run) ----------

        /// <summary>Modality of the converted image series ("CT", "MR", "PT"). Default "CT".</summary>
        public string ImageModality { get; set; } = "CT";

        /// <summary>
        /// DICOM PatientPosition (0018,5100). One of HFS / FFS / HFP / FFP / etc. Required (Type 2C)
        /// for CT/MR/PT image storage SOPs — TPS systems (Eclipse, RayStation, ...) reject the
        /// series with "Unknown patient position" when this tag is missing. Default "HFS"
        /// (head-first supine) covers the overwhelming majority of clinical scans.
        /// </summary>
        public string PatientPosition { get; set; } = "HFS";

        /// <summary>SeriesInstanceUID of the image series written from image.nii.gz.</summary>
        public string ImageSeriesInstanceUid { get; set; } = "";

        /// <summary>Per-slice SOPInstanceUIDs, ordered by ascending z-index (slice 0 first).</summary>
        public List<string> ImageSopInstanceUids { get; set; } = new List<string>();

        /// <summary>DICOM RescaleSlope written to each image slice. 1.0 by default.</summary>
        public double ImageRescaleSlope { get; set; } = 1.0;

        /// <summary>
        /// DICOM RescaleIntercept written to each image slice. 0.0 by default.
        /// For raw CT data stored as offset-from-air, this is typically -1024 with slope 1.
        /// </summary>
        public double ImageRescaleIntercept { get; set; } = 0.0;
    }
}
