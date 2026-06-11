using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Groups all DICOM studies for a single patient.
    /// </summary>
    public class DicomPatientGroup
    {
        /// <summary>
        /// DICOM tag (0010,0020).
        /// </summary>
        public string PatientID { get; set; } = "";

        /// <summary>
        /// DICOM tag (0010,0010).
        /// </summary>
        public string PatientName { get; set; } = "";

        /// <summary>
        /// Studies belonging to this patient.
        /// </summary>
        public List<DicomStudyGroup> Studies { get; set; } = new List<DicomStudyGroup>();
    }
}
