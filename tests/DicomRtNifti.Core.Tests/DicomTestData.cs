using System.Globalization;
using System.IO;
using FellowOakDicom;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Helpers for writing minimal, valid DICOM files to a temp directory so the
    /// scanner / linker services can be exercised without real patient data.
    /// </summary>
    internal static class DicomTestData
    {
        public static string NewTempDir()
        {
            // No Directory.GetRandomFileName collisions: combine temp + a GUID-free
            // random name (Path.GetRandomFileName is deterministic-safe here).
            string dir = Path.Combine(Path.GetTempPath(), "drt_test_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Writes a single image slice with the given identifiers and modality.
        /// </summary>
        public static void WriteImageSlice(
            string dir, string fileName, string modality,
            string patientId, string studyUid, string seriesUid, string frameUid, double z)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, modality },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, modality + " series" },
            };
            ds.Add(DicomTag.ImagePositionPatient,
                "0", "0", z.ToString(CultureInfo.InvariantCulture));

            new DicomFile(ds).Save(Path.Combine(dir, fileName));
        }

        /// <summary>
        /// Writes a minimal (metadata-only) RT-DOSE file. The scanner reads only metadata
        /// (SkipLargeTags), so no pixel data is needed to exercise dose linking.
        /// </summary>
        public static void WriteRtDose(
            string dir, string fileName,
            string patientId, string studyUid, string seriesUid, string frameUid)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "RTDOSE" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, "dose" },
            };
            new DicomFile(ds).Save(Path.Combine(dir, fileName));
        }

        public static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    }
}
