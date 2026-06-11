using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Groups all DICOM series within a single study.
    /// </summary>
    public class DicomStudyGroup
    {
        /// <summary>
        /// DICOM tag (0020,000D).
        /// </summary>
        public string StudyInstanceUID { get; set; } = "";

        /// <summary>
        /// DICOM tag (0008,1030).
        /// </summary>
        public string StudyDescription { get; set; } = "";

        /// <summary>
        /// DICOM tag (0008,0020).
        /// </summary>
        public string StudyDate { get; set; } = "";

        /// <summary>
        /// Series belonging to this study.
        /// </summary>
        public List<DicomSeriesGroup> Series { get; set; } = new List<DicomSeriesGroup>();
    }
}
