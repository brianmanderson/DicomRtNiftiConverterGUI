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
        /// <param name="referencedSeriesUid">
        /// When supplied, nests a ReferencedFrameOfReferenceSequence &gt; RTReferencedStudySequence
        /// &gt; RTReferencedSeriesSequence carrying this SeriesInstanceUID, so the scanner can
        /// resolve the structure set to a specific image series rather than falling back to the
        /// frame of reference. Omit to model the (common) structure sets that carry no such link.
        /// </param>
        public static string WriteRtStruct(
            string dir, string fileName,
            string patientId, string studyUid, string seriesUid, string frameUid,
            string structureSetLabel, string[] roiNames,
            string referencedSeriesUid = null,
            string seriesDescription = null)
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
                // Real structure sets name themselves here, not just in StructureSetLabel — and
                // SeriesDescription is what selection filters match on, so it must be settable.
                { DicomTag.SeriesDescription, seriesDescription ?? "structures" },
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

            if (!string.IsNullOrEmpty(referencedSeriesUid))
            {
                var refSeries = new DicomDataset
                {
                    { DicomTag.SeriesInstanceUID, referencedSeriesUid },
                };
                var refStudy = new DicomDataset
                {
                    { DicomTag.ReferencedSOPInstanceUID, studyUid },
                };
                refStudy.Add(new DicomSequence(DicomTag.RTReferencedSeriesSequence, refSeries));

                var refFrame = new DicomDataset
                {
                    { DicomTag.FrameOfReferenceUID, frameUid },
                };
                refFrame.Add(new DicomSequence(DicomTag.RTReferencedStudySequence, refStudy));

                ds.Add(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence, refFrame));
            }

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

        /// <summary>
        /// Writes an axial CT series that SimpleITK's ImageSeriesReader can actually load: real
        /// 16-bit PixelData, IOP/IPP/PixelSpacing, one slice per <paramref name="sliceCount"/> at a
        /// uniform <paramref name="sliceGapMm"/>. With a 1 mm grid whose origin is (0,0,0), a
        /// physical coordinate in mm equals its continuous voxel index, which keeps the contour
        /// fixtures below readable.
        /// </summary>
        /// <returns>The folder holding the slices.</returns>
        public static string WriteCtSeriesWithPixels(
            string dir, string seriesUid, string frameUid,
            int sliceCount = 4, int size = 16,
            double inPlaneSpacingMm = 1.0, double sliceGapMm = 1.0,
            string studyUid = null, string patientId = "PAT001")
        {
            studyUid = studyUid ?? NewUid();
            Directory.CreateDirectory(dir);

            var pixels = new ushort[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 1000;
            byte[] bytes = new byte[pixels.Length * 2];
            System.Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);

            for (int s = 0; s < sliceCount; s++)
            {
                double z = s * sliceGapMm;
                var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                {
                    { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                    { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                    { DicomTag.PatientID, patientId },
                    { DicomTag.PatientName, "Test^Patient" },
                    { DicomTag.StudyInstanceUID, studyUid },
                    { DicomTag.SeriesInstanceUID, seriesUid },
                    { DicomTag.FrameOfReferenceUID, frameUid },
                    { DicomTag.StudyID, "1" },
                    { DicomTag.SeriesNumber, "1" },
                    { DicomTag.InstanceNumber, (s + 1).ToString(CultureInfo.InvariantCulture) },
                    { DicomTag.Modality, "CT" },
                    { DicomTag.SeriesDescription, "CT series" },
                    { DicomTag.SamplesPerPixel, (ushort)1 },
                    { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                    { DicomTag.BitsAllocated, (ushort)16 },
                    { DicomTag.BitsStored, (ushort)16 },
                    { DicomTag.HighBit, (ushort)15 },
                    { DicomTag.PixelRepresentation, (ushort)0 },
                    { DicomTag.Rows, (ushort)size },
                    { DicomTag.Columns, (ushort)size },
                    { DicomTag.RescaleIntercept, "0" },
                    { DicomTag.RescaleSlope, "1" },
                };

                ds.Add(DicomTag.ImagePositionPatient,
                    "0", "0", z.ToString(CultureInfo.InvariantCulture));
                ds.Add(DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0");
                ds.Add(DicomTag.PixelSpacing,
                    inPlaneSpacingMm.ToString(CultureInfo.InvariantCulture),
                    inPlaneSpacingMm.ToString(CultureInfo.InvariantCulture));
                ds.Add(DicomTag.SliceThickness, sliceGapMm.ToString(CultureInfo.InvariantCulture));
                ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));

                new DicomFile(ds).Save(Path.Combine(dir, $"ct_{s:D3}.dcm"));
            }

            return dir;
        }

        /// <summary>
        /// Writes an RTSTRUCT carrying one CLOSED_PLANAR contour per entry in
        /// <paramref name="roiContours"/> — ROI name paired with a flat ContourData array
        /// [x0,y0,z0, x1,y1,z1, ...] in patient mm. Enough to drive
        /// <see cref="DicomRtNifti.Core.Services.RtStructMaskService"/> end to end.
        /// </summary>
        public static string WriteRtStructWithContours(
            string dir, string fileName,
            string studyUid, string seriesUid, string frameUid,
            IList<KeyValuePair<string, double[]>> roiContours,
            string patientId = "PAT001")
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
                { DicomTag.StructureSetLabel, "TEST" },
            };

            var roiItems = new List<DicomDataset>();
            var contourItems = new List<DicomDataset>();

            int roiNumber = 1;
            foreach (var entry in roiContours)
            {
                roiItems.Add(new DicomDataset
                {
                    { DicomTag.ROINumber, roiNumber.ToString(CultureInfo.InvariantCulture) },
                    { DicomTag.ReferencedFrameOfReferenceUID, frameUid },
                    { DicomTag.ROIName, entry.Key },
                });

                double[] points = entry.Value;
                string[] contourData = new string[points.Length];
                for (int i = 0; i < points.Length; i++)
                    contourData[i] = points[i].ToString("0.####", CultureInfo.InvariantCulture);

                var contour = new DicomDataset
                {
                    { DicomTag.ContourGeometricType, "CLOSED_PLANAR" },
                    { DicomTag.NumberOfContourPoints, (points.Length / 3).ToString(CultureInfo.InvariantCulture) },
                };
                contour.Add(DicomTag.ContourData, contourData);

                var roiContour = new DicomDataset
                {
                    { DicomTag.ReferencedROINumber, roiNumber.ToString(CultureInfo.InvariantCulture) },
                };
                roiContour.Add(DicomTag.ROIDisplayColor, "255", "0", "0");
                roiContour.Add(new DicomSequence(DicomTag.ContourSequence, contour));
                contourItems.Add(roiContour);

                roiNumber++;
            }

            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, roiItems.ToArray()));
            ds.Add(new DicomSequence(DicomTag.ROIContourSequence, contourItems.ToArray()));

            string path = Path.Combine(dir, fileName);
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>
        /// A closed axial square contour, corners at (<paramref name="min"/>,<paramref name="min"/>)
        /// and (<paramref name="max"/>,<paramref name="max"/>) mm, on the plane z = <paramref name="z"/>.
        /// </summary>
        public static double[] SquareContour(double min, double max, double z)
        {
            return new[]
            {
                min, min, z,
                max, min, z,
                max, max, z,
                min, max, z,
            };
        }

        public static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    }
}
