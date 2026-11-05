using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>One contour to write: its type and its ContourData as raw strings (null omits the element).</summary>
    internal sealed class TestContour
    {
        public string Type { get; set; } = "CLOSED_PLANAR";
        public string[] Data { get; set; }

        public static TestContour Of(string type, params double[] points) => new TestContour
        {
            Type = type,
            Data = points.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)).ToArray(),
        };

        public static TestContour Closed(params double[] points) => Of("CLOSED_PLANAR", points);

        /// <summary>ContourData that is not numbers: fo-dicom refuses to parse it.</summary>
        public static TestContour Unparseable() => new TestContour { Data = new[] { "a", "b", "c" } };

        /// <summary>ContourData of <paramref name="values"/> numbers, fewer than one point when below three.</summary>
        public static TestContour Short(int values) => new TestContour
        {
            Data = Enumerable.Range(1, values).Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray(),
        };
    }

    /// <summary>One ROI to write; null <see cref="Contours"/> writes it without a ROIContourSequence item.</summary>
    internal sealed class TestRoi
    {
        public TestRoi(string name, params TestContour[] contours)
        {
            Name = name;
            Contours = contours;
        }

        public string Name { get; }
        public TestContour[] Contours { get; set; }
    }

    /// <summary>
    /// Structure sets and doses for the contours tier's tests: any geometric type, unparseable and
    /// short ContourData, ROIs without contours, doses with a grid and pixels.
    /// </summary>
    internal static class ContourTestData
    {
        public static string WriteRtStruct(string dir, string fileName, string patientId, string studyUid,
            string seriesUid, string frameUid, IEnumerable<TestRoi> rois, string referencedSeriesUid = null)
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
            int number = 1;
            foreach (var roi in rois)
            {
                string n = number.ToString(CultureInfo.InvariantCulture);
                roiItems.Add(new DicomDataset
                {
                    { DicomTag.ROINumber, n },
                    { DicomTag.ReferencedFrameOfReferenceUID, frameUid },
                    { DicomTag.ROIName, roi.Name },
                });
                if (roi.Contours != null)
                {
                    var contours = roi.Contours.Select(c =>
                    {
                        var item = new DicomDataset { { DicomTag.ContourGeometricType, c.Type } };
                        if (c.Data != null)
                        {
                            // Deliberately invalid content for the unparseable case; fo-dicom's
                            // validator would refuse it before the reader could.
#pragma warning disable CS0618
                            item.AutoValidate = false;
#pragma warning restore CS0618
                            item.Add(DicomTag.NumberOfContourPoints, (c.Data.Length / 3).ToString(CultureInfo.InvariantCulture));
                            item.Add(DicomTag.ContourData, c.Data);
                        }
                        return item;
                    }).ToArray();
                    var roiContour = new DicomDataset { { DicomTag.ReferencedROINumber, n } };
                    // A sequence validates its items when added; the unparseable case must survive that.
#pragma warning disable CS0618
                    roiContour.AutoValidate = false;
#pragma warning restore CS0618
                    roiContour.Add(DicomTag.ROIDisplayColor, "255", "0", "0");
                    roiContour.Add(new DicomSequence(DicomTag.ContourSequence, contours));
                    contourItems.Add(roiContour);
                }
                number++;
            }
#pragma warning disable CS0618
            ds.AutoValidate = false;
#pragma warning restore CS0618
            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, roiItems.ToArray()));
            ds.Add(new DicomSequence(DicomTag.ROIContourSequence, contourItems.ToArray()));

            if (!string.IsNullOrEmpty(referencedSeriesUid))
            {
                var refSeries = new DicomDataset { { DicomTag.SeriesInstanceUID, referencedSeriesUid } };
                var refStudy = new DicomDataset { { DicomTag.ReferencedSOPInstanceUID, studyUid } };
                refStudy.Add(new DicomSequence(DicomTag.RTReferencedSeriesSequence, refSeries));
                var refFrame = new DicomDataset { { DicomTag.FrameOfReferenceUID, frameUid } };
                refFrame.Add(new DicomSequence(DicomTag.RTReferencedStudySequence, refStudy));
                ds.Add(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence, refFrame));
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>
        /// An RTDOSE with a full grid header and 32-bit pixels: <paramref name="columns"/> ×
        /// <paramref name="rows"/> × frames, frame f's voxels all <c>f + 1</c> stored, scaled by 0.01.
        /// </summary>
        public static DicomDataset Dose(string patientId, string studyUid, string seriesUid, string frameUid,
            double[] position, double[] orientation, double[] pixelSpacing, int rows, int columns, double[] frameOffsets,
            string units = "GY")
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
                { DicomTag.DoseUnits, units },
                { DicomTag.DoseType, "PHYSICAL" },
                { DicomTag.DoseSummationType, "PLAN" },
                { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.BitsAllocated, (ushort)32 },
                { DicomTag.BitsStored, (ushort)32 },
                { DicomTag.HighBit, (ushort)31 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.Rows, (ushort)rows },
                { DicomTag.Columns, (ushort)columns },
                { DicomTag.NumberOfFrames, frameOffsets.Length.ToString(CultureInfo.InvariantCulture) },
                { DicomTag.DoseGridScaling, "0.01" },
            };
            ds.Add(DicomTag.ImagePositionPatient, Strings(position));
            ds.Add(DicomTag.ImageOrientationPatient, Strings(orientation));
            ds.Add(DicomTag.PixelSpacing, Strings(pixelSpacing));
            // GridFrameOffsetVector is 2-n: a single-frame dose omits it.
            if (frameOffsets.Length > 1)
                ds.Add(DicomTag.GridFrameOffsetVector, Strings(frameOffsets));

            var stored = new uint[rows * columns * frameOffsets.Length];
            for (int i = 0; i < stored.Length; i++) stored[i] = (uint)(i / (rows * columns) + 1);
            byte[] bytes = new byte[stored.Length * 4];
            System.Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));
            return ds;
        }

        public static string WriteDose(string dir, string fileName, DicomDataset dose)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            new DicomFile(dose).Save(path);
            return path;
        }

        /// <summary>
        /// One image slice's header, no pixels: <paramref name="size"/> square at 1 mm, axial,
        /// ImagePositionPatient (0, 0, z). Enough for the scan and for <see cref="Analysis.ImageGrid"/>.
        /// </summary>
        public static void WriteSlice(string dir, string fileName, string patientId, string studyUid, string seriesUid,
            string frameUid, double z, int size = 16)
        {
            Directory.CreateDirectory(dir);
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "CT" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, "CT" },
                { DicomTag.InstanceNumber, "1" },
                { DicomTag.Rows, (ushort)size },
                { DicomTag.Columns, (ushort)size },
            };
            ds.Add(DicomTag.ImagePositionPatient, "0", "0", z.ToString(CultureInfo.InvariantCulture));
            ds.Add(DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0");
            ds.Add(DicomTag.PixelSpacing, "1", "1");
            ds.Add(DicomTag.SliceThickness, "1");
            new DicomFile(ds).Save(Path.Combine(dir, fileName));
        }

        private static string[] Strings(double[] values) =>
            values.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)).ToArray();
    }
}
