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

        public static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    }
}
