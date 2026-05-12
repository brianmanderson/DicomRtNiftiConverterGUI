using System.Collections.Generic;

namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// Aggregate result of a recursive DICOM directory scan. Carries both the parsed patient
    /// hierarchy and a count of files that could not be opened so callers can surface the
    /// failure instead of silently reporting "0 patients".
    /// </summary>
    public class DicomScanResult
    {
        public List<DicomPatientGroup> Patients { get; set; } = new List<DicomPatientGroup>();

        /// <summary>
        /// Total candidate files the scanner enumerated (everything under the input folder).
        /// </summary>
        public int TotalFileCount { get; set; }

        /// <summary>
        /// Files that threw an unexpected exception while opening (PathTooLong, Unauthorized,
        /// IO, etc.). Excludes files that were validly identified as non-DICOM.
        /// </summary>
        public int SkippedErrorCount { get; set; }

        /// <summary>
        /// Up to a small number of sample "path: error" strings to aid diagnosis when
        /// SkippedErrorCount > 0. Capped to avoid unbounded growth on bulk failures.
        /// </summary>
        public List<string> SkippedErrorSamples { get; set; } = new List<string>();
    }
}
