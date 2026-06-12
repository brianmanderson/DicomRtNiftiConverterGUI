using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;

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

        /// <summary>
        /// Writes a minimal RTSTRUCT with a StructureSetROISequence (one item per name) and a
        /// StructureSetLabel, enough to exercise ROI-name/count metadata extraction. Returns the path.
        /// </summary>
        public static string WriteRtStruct(
            string dir, string fileName,
            string patientId, string studyUid, string seriesUid, string frameUid,
            string structureSetLabel, string[] roiNames)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "RTSTRUCT" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, "structures" },
                { DicomTag.StructureSetLabel, structureSetLabel },
            };

            var items = new List<DicomDataset>();
            int roiNumber = 1;
            foreach (var name in roiNames)
            {
                items.Add(new DicomDataset
                {
                    { DicomTag.ROINumber, roiNumber.ToString(CultureInfo.InvariantCulture) },
                    { DicomTag.ReferencedFrameOfReferenceUID, frameUid },
                    { DicomTag.ROIName, name },
                });
                roiNumber++;
            }
            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, items.ToArray()));

            string path = Path.Combine(dir, fileName);
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>
        /// Writes an RTDOSE with native 32-bit unsigned pixel data (a 1×N×1 grid of
        /// <paramref name="storedValues"/>), DoseGridScaling, and optional PixelSpacing /
        /// GridFrameOffsetVector — enough to exercise max-dose and dose-voxel-size extraction.
        /// Bytes are written little-endian to match the explicit-VR-LE transfer syntax. Returns the path.
        /// </summary>
        public static string WriteRtDose32Bit(
            string dir, string fileName,
            string patientId, string studyUid, string seriesUid, string frameUid,
            double doseGridScaling, uint[] storedValues,
            double[] pixelSpacing = null, double[] gridFrameOffsetVector = null)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "RTDOSE" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, "dose" },
                { DicomTag.DoseUnits, "GY" },
                { DicomTag.DoseType, "PHYSICAL" },
                { DicomTag.DoseSummationType, "PLAN" },
                { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.BitsAllocated, (ushort)32 },
                { DicomTag.BitsStored, (ushort)32 },
                { DicomTag.HighBit, (ushort)31 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.Rows, (ushort)1 },
                { DicomTag.Columns, (ushort)storedValues.Length },
                { DicomTag.NumberOfFrames, "1" },
                { DicomTag.DoseGridScaling, doseGridScaling.ToString(CultureInfo.InvariantCulture) },
            };

            if (pixelSpacing != null && pixelSpacing.Length >= 2)
                ds.Add(DicomTag.PixelSpacing,
                    pixelSpacing[0].ToString(CultureInfo.InvariantCulture),
                    pixelSpacing[1].ToString(CultureInfo.InvariantCulture));

            if (gridFrameOffsetVector != null && gridFrameOffsetVector.Length > 0)
            {
                string[] gfov = new string[gridFrameOffsetVector.Length];
                for (int i = 0; i < gfov.Length; i++)
                    gfov[i] = gridFrameOffsetVector[i].ToString(CultureInfo.InvariantCulture);
                ds.Add(DicomTag.GridFrameOffsetVector, gfov);
            }

            byte[] bytes = new byte[storedValues.Length * 4];
            System.Buffer.BlockCopy(storedValues, 0, bytes, 0, bytes.Length);
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));

            string path = Path.Combine(dir, fileName);
            new DicomFile(ds).Save(path);
            return path;
        }

        public static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    }
}
