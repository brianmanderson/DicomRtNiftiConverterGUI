using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using itk.simple;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The geometry of a synthetic RT-DOSE grid, in DICOM terms. Spacing is given per axis so
    /// that a swap of PixelSpacing's (row, column) order shows up as a wrong x or y spacing.
    /// </summary>
    internal sealed class DoseGrid
    {
        public int Columns;
        public int Rows;
        public int Frames;

        /// <summary>ImagePositionPatient of the first frame, mm.</summary>
        public double[] Origin;

        /// <summary>Distance between adjacent columns (x), PixelSpacing[1].</summary>
        public double ColumnSpacing;

        /// <summary>Distance between adjacent rows (y), PixelSpacing[0].</summary>
        public double RowSpacing;

        /// <summary>Step of GridFrameOffsetVector, along the slice normal.</summary>
        public double FrameSpacing;

        /// <summary>ImageOrientationPatient: row cosine then column cosine.</summary>
        public double[] Iop = { 1, 0, 0, 0, 1, 0 };

        public int VoxelCount => Columns * Rows * Frames;

        /// <summary>Row cosine × column cosine: the direction GridFrameOffsetVector runs along.</summary>
        public double[] Normal => new[]
        {
            Iop[1] * Iop[5] - Iop[2] * Iop[4],
            Iop[2] * Iop[3] - Iop[0] * Iop[5],
            Iop[0] * Iop[4] - Iop[1] * Iop[3],
        };

        /// <summary>ITK's row-major direction matrix: columns are the row, column and normal cosines.</summary>
        public double[] ItkDirection
        {
            get
            {
                var n = Normal;
                return new[]
                {
                    Iop[0], Iop[3], n[0],
                    Iop[1], Iop[4], n[1],
                    Iop[2], Iop[5], n[2],
                };
            }
        }
    }

    /// <summary>
    /// Writes multi-frame RT-DOSE files with real pixel data, so the dose path can be exercised
    /// end to end through SimpleITK without committed fixtures or patient data.
    /// </summary>
    internal static class SyntheticRtDose
    {
        public static string Write(
            string dir, string fileName, DoseGrid grid, uint[] stored, int bitsAllocated,
            double doseGridScaling, string doseUnits,
            string patientId, string studyUid, string seriesUid, string frameUid,
            string seriesDescription = "dose",
            Action<DicomDataset> customize = null)
        {
            if (stored.Length != grid.VoxelCount)
                throw new ArgumentException("stored value count does not match the grid", nameof(stored));
            if (bitsAllocated != 16 && bitsAllocated != 32)
                throw new ArgumentOutOfRangeException(nameof(bitsAllocated));

            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Dose^Phantom" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "RTDOSE" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, seriesDescription },
                { DicomTag.DoseUnits, doseUnits },
                { DicomTag.DoseType, "PHYSICAL" },
                { DicomTag.DoseSummationType, "PLAN" },
                { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.BitsAllocated, (ushort)bitsAllocated },
                { DicomTag.BitsStored, (ushort)bitsAllocated },
                { DicomTag.HighBit, (ushort)(bitsAllocated - 1) },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.Rows, (ushort)grid.Rows },
                { DicomTag.Columns, (ushort)grid.Columns },
                { DicomTag.NumberOfFrames, grid.Frames.ToString(CultureInfo.InvariantCulture) },
                { DicomTag.FrameIncrementPointer, DicomTag.GridFrameOffsetVector },
                { DicomTag.DoseGridScaling, Ds(doseGridScaling) },
            };
            ds.Add(DicomTag.ImagePositionPatient, grid.Origin.Select(Ds).ToArray());
            ds.Add(DicomTag.ImageOrientationPatient, grid.Iop.Select(Ds).ToArray());
            ds.Add(DicomTag.PixelSpacing, Ds(grid.RowSpacing), Ds(grid.ColumnSpacing));
            ds.Add(DicomTag.SliceThickness, Ds(grid.FrameSpacing));
            ds.Add(DicomTag.GridFrameOffsetVector,
                Enumerable.Range(0, grid.Frames).Select(k => Ds(k * grid.FrameSpacing)).ToArray());

            byte[] bytes;
            if (bitsAllocated == 32)
            {
                bytes = new byte[stored.Length * 4];
                Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
            }
            else
            {
                var narrow = new ushort[stored.Length];
                for (int i = 0; i < stored.Length; i++)
                    narrow[i] = checked((ushort)stored[i]);
                bytes = new byte[narrow.Length * 2];
                Buffer.BlockCopy(narrow, 0, bytes, 0, bytes.Length);
            }
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));

            customize?.Invoke(ds);

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>
        /// Stored values that cover the whole range of <paramref name="bitsAllocated"/>, in a
        /// scattered pattern so a transposed or flipped axis cannot go unnoticed. Voxel 0 is zero
        /// and one voxel holds the maximum, as a real plan dose has both.
        /// </summary>
        public static uint[] FullRangeValues(DoseGrid grid, int bitsAllocated)
        {
            ulong modulus = bitsAllocated == 32 ? 4294967296UL : 65536UL;
            var values = new uint[grid.VoxelCount];
            for (int i = 0; i < values.Length; i++)
                values[i] = (uint)(((ulong)i * 2654435761UL + 977UL) % modulus);
            values[0] = 0;
            values[values.Length / 2] = (uint)(modulus - 1);
            return values;
        }

        /// <summary>A DS value: ten significant digits keep every value within VR DS's 16 characters.</summary>
        public static string Ds(double value) => value.ToString("G10", CultureInfo.InvariantCulture);

        /// <summary>
        /// The double a reader recovers from <paramref name="value"/> once it is written as DS.
        /// Expectations are computed from this, so a test compares against what is in the file.
        /// </summary>
        public static double AsWritten(double value) => double.Parse(Ds(value), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// RT-DOSE accuracy in both directions (W2-E): RT-DOSE → NIfTI applies DoseGridScaling and
    /// keeps the dose's own grid; NIfTI → RT-DOSE re-derives DoseGridScaling and keeps that grid;
    /// the round trip reproduces every voxel to within one quantization step of the file it
    /// writes; and the written RT-DOSE carries the attributes the RT Dose IOD requires.
    /// </summary>
    public class RtDoseRoundTripTests
    {
        private readonly ITestOutputHelper _output;

        public RtDoseRoundTripTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static readonly DoseGrid AxialGrid = new DoseGrid
        {
            Columns = 7,
            Rows = 5,
            Frames = 4,
            Origin = new[] { -31.25, 12.5, -7.0 },
            ColumnSpacing = 2.5,
            RowSpacing = 2.0,
            FrameSpacing = 3.0,
        };

        // In-plane rotation by 90° about the patient z axis: the row cosine points along +y and
        // the column cosine along -x, so a writer that mixes up IOP and the direction matrix's
        // columns, or reads the matrix in the wrong order, writes the dose rotated.
        private static readonly DoseGrid RotatedGrid = new DoseGrid
        {
            Columns = 6,
            Rows = 4,
            Frames = 3,
            Origin = new[] { 40.0, -18.5, 102.25 },
            ColumnSpacing = 1.5,
            RowSpacing = 2.25,
            FrameSpacing = 2.5,
            Iop = new double[] { 0, 1, 0, -1, 0, 0 },
        };

        public static IEnumerable<object[]> Encodings()
        {
            // 16-bit with a coarse step, as older planning systems write; 32-bit across its full
            // range, where the step is ~1e-8 Gy and any float32 detour is far coarser than it.
            yield return new object[] { 16, 0.001 };
            yield return new object[] { 32, SyntheticRtDose.AsWritten(70.0 / uint.MaxValue) };
        }

        // ── RT-DOSE → NIfTI ────────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Encodings))]
        public void Forward_AppliesDoseGridScalingOnce_AndKeepsTheDoseGrid(int bitsAllocated, double scaling)
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var grid = AxialGrid;
                uint[] stored = SyntheticRtDose.FullRangeValues(grid, bitsAllocated);
                var dose = WriteDoseSeries(root, grid, stored, bitsAllocated, scaling, "GY");

                string niftiPath = Forward(dose, Path.Combine(root, "out"));

                using (var img = NiftiConversionService.ReadImageSafely(niftiPath))
                {
                    AssertGrid(grid, img);
                    double[] values = ReadDoubles(img);
                    double maxErr = MaxAbsError(values, stored, scaling);
                    _output.WriteLine($"{bitsAllocated}-bit forward: max |NIfTI - stored*scaling| = {maxErr:E3} Gy");
                    // The NIfTI is float64; anything beyond rounding means the scale was applied
                    // twice, not at all, or to the wrong voxel.
                    Assert.True(maxErr <= 1e-12 * Math.Max(1.0, stored.Max() * scaling),
                        $"forward dose differs from stored*DoseGridScaling by up to {maxErr:E3} Gy");
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Forward_RotatedDoseGrid_KeepsOrientation()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var grid = RotatedGrid;
                uint[] stored = SyntheticRtDose.FullRangeValues(grid, 16);
                var dose = WriteDoseSeries(root, grid, stored, 16, 0.002, "GY");

                string niftiPath = Forward(dose, Path.Combine(root, "out"));

                using (var img = NiftiConversionService.ReadImageSafely(niftiPath))
                {
                    AssertGrid(grid, img);
                    Assert.True(MaxAbsError(ReadDoubles(img), stored, 0.002) <= 1e-12 * 131.07);
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Forward_CentigrayDose_IsWrittenInGray()
        {
            // The NIfTI carries no unit and the reverse writer labels every dose GY, so a cGy
            // dose exported as cGy numbers comes back as a 100x overdose in Gy. The NIfTI dose
            // contract is therefore Gy.
            string root = DicomTestData.NewTempDir();
            try
            {
                var grid = AxialGrid;
                uint[] stored = SyntheticRtDose.FullRangeValues(grid, 16);
                var dose = WriteDoseSeries(root, grid, stored, 16, 0.1, "CGY");

                string niftiPath = Forward(dose, Path.Combine(root, "out"));

                using (var img = NiftiConversionService.ReadImageSafely(niftiPath))
                {
                    double maxErr = MaxAbsError(ReadDoubles(img), stored, 0.1 / 100.0);
                    Assert.True(maxErr <= 1e-12 * 65.535,
                        $"a CGY dose was not converted to Gy (max error {maxErr:E3} Gy)");
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ── RT-DOSE → NIfTI → RT-DOSE ──────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Encodings))]
        public void RoundTrip_PreservesGridUnitsAndDose_WithinOneQuantizationStep(int bitsAllocated, double scaling)
        {
            foreach (var grid in new[] { AxialGrid, RotatedGrid })
            {
                string root = DicomTestData.NewTempDir();
                try
                {
                    uint[] stored = SyntheticRtDose.FullRangeValues(grid, bitsAllocated);
                    var dose = WriteDoseSeries(root, grid, stored, bitsAllocated, scaling, "GY");
                    string exportDir = Path.Combine(root, "out");
                    Forward(dose, exportDir);

                    // NIfTI-only reverse, as the GUI runs it on an export folder with no DICOM.
                    var metadata = new NiftiMetadataService().LoadOrSynthesize(exportDir);
                    var written = new RtDoseWriterService().ConvertDoseFolderToRtDoses(
                        exportDir, null, null, CancellationToken.None,
                        useStableHashNames: true, skipIfExists: false, metadata: metadata);
                    var back = DicomFile.Open(Assert.Single(written)).Dataset;

                    AssertRtDoseGrid(grid, back);
                    Assert.Equal("GY", back.GetString(DicomTag.DoseUnits));
                    Assert.Equal(metadata.FrameOfReferenceUid, back.GetString(DicomTag.FrameOfReferenceUID));

                    double backScaling = back.GetSingleValue<double>(DicomTag.DoseGridScaling);
                    uint[] backStored = ReadStoredUInt32(back);
                    double maxDose = stored.Max() * scaling;

                    // DoseGridScaling is re-derived from the volume's maximum: the brightest voxel
                    // uses the full 32-bit range.
                    Assert.Equal(uint.MaxValue, backStored.Max());
                    Assert.Equal(maxDose / uint.MaxValue, backScaling, maxDose / uint.MaxValue * 1e-9);

                    double maxErr = 0;
                    for (int i = 0; i < stored.Length; i++)
                        maxErr = Math.Max(maxErr, Math.Abs(backStored[i] * backScaling - stored[i] * scaling));
                    _output.WriteLine(
                        $"{bitsAllocated}-bit, IOP [{string.Join(",", grid.Iop)}]: source step {scaling:E3} Gy, " +
                        $"written step {backScaling:E3} Gy, max abs round-trip error {maxErr:E3} Gy " +
                        $"({maxErr / backScaling:F3} written steps)");

                    Assert.True(maxErr <= backScaling,
                        $"round-trip error {maxErr:E3} Gy exceeds one quantization step of the written RT-DOSE ({backScaling:E3} Gy)");
                    Assert.True(maxErr <= scaling,
                        $"round-trip error {maxErr:E3} Gy exceeds one quantization step of the source RT-DOSE ({scaling:E3} Gy)");
                }
                finally
                {
                    TryDelete(root);
                }
            }
        }

        [Fact]
        public async Task CohortExport_DoseOnADifferentGridThanTheCt_KeepsItsOwnGridBothWays()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                string input = Path.Combine(root, "in");
                string studyUid = DicomTestData.NewUid();
                string frameUid = DicomTestData.NewUid();
                string ctSeriesUid = DicomTestData.NewUid();

                // CT: 16 × 16 × 4 at 1 mm from the origin. Dose: coarser, offset, and smaller.
                DicomTestData.WriteCtSeriesWithPixels(
                    Path.Combine(input, "ct"), ctSeriesUid, frameUid, sliceCount: 4, size: 16,
                    studyUid: studyUid, patientId: "DOSEGRID01");
                var grid = new DoseGrid
                {
                    Columns = 5,
                    Rows = 3,
                    Frames = 2,
                    Origin = new[] { 1.75, 3.5, 0.5 },
                    ColumnSpacing = 2.5,
                    RowSpacing = 4.0,
                    FrameSpacing = 2.0,
                };
                const double scaling = 0.0005;
                uint[] stored = SyntheticRtDose.FullRangeValues(grid, 16);
                SyntheticRtDose.Write(Path.Combine(input, "dose"), "dose.dcm", grid, stored, 16, scaling, "GY",
                    "DOSEGRID01", studyUid, DicomTestData.NewUid(), frameUid, seriesDescription: "Plan dose");

                var scan = await new DicomScannerService().ScanFolderAsync(input, null, CancellationToken.None);
                var options = new CohortExportOptions
                {
                    InputRoot = input,
                    OutputRoot = Path.Combine(root, "out"),
                    ExportStructures = false,
                };
                var plan = CohortExportService.BuildPlan(scan, options, null);
                var planned = Assert.Single(plan.Series);
                Assert.Single(planned.RtDoses);

                var result = await new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
                    .ExecuteAsync(plan, options, null, null, CancellationToken.None);
                Assert.Equal(0, result.FailedCount);
                var series = Assert.Single(result.Series);
                string doseNifti = Path.Combine(result.OutputRoot, Assert.Single(series.Doses));
                string seriesDir = Path.Combine(result.OutputRoot, series.OutputDir);

                using (var ct = NiftiConversionService.ReadImageSafely(Path.Combine(seriesDir, "image.nii.gz")))
                using (var doseImg = NiftiConversionService.ReadImageSafely(doseNifti))
                {
                    Assert.Equal(new uint[] { 16, 16, 4 }, ct.GetSize().ToArray());
                    AssertGrid(grid, doseImg);
                    Assert.True(MaxAbsError(ReadDoubles(doseImg), stored, scaling) <= 1e-12 * 32.8);
                }

                // Reverse against the CT as the reference series: the dose keeps its own grid
                // and takes the CT's study and frame of reference.
                var written = new RtDoseWriterService().ConvertDoseFolderToRtDoses(
                    seriesDir, planned.Image, null, CancellationToken.None, useStableHashNames: true);
                var back = DicomFile.Open(Assert.Single(written)).Dataset;
                AssertRtDoseGrid(grid, back);
                Assert.Equal(frameUid, back.GetString(DicomTag.FrameOfReferenceUID));
                Assert.Equal(studyUid, back.GetString(DicomTag.StudyInstanceUID));
                Assert.Equal("DOSEGRID01", back.GetString(DicomTag.PatientID));

                double backScaling = back.GetSingleValue<double>(DicomTag.DoseGridScaling);
                uint[] backStored = ReadStoredUInt32(back);
                double maxErr = 0;
                for (int i = 0; i < stored.Length; i++)
                    maxErr = Math.Max(maxErr, Math.Abs(backStored[i] * backScaling - stored[i] * scaling));
                _output.WriteLine($"dose-on-own-grid round trip: max abs error {maxErr:E3} Gy, written step {backScaling:E3} Gy");
                Assert.True(maxErr <= backScaling);
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ── NIfTI → RT-DOSE: the RT Dose IOD ───────────────────────────────────────────────

        public static IEnumerable<object[]> ReferenceModes()
        {
            yield return new object[] { true };
            yield return new object[] { false };
        }

        /// <summary>
        /// The GUI-only NIfTI → RT-DOSE write, checked against the attributes PS3.3 A.18 (RT Dose
        /// IOD) requires of a grid dose: Type 1 present and non-empty, Type 2 present, and the 1C
        /// attributes whose condition holds for a multi-frame grid with pixel data. The reference
        /// CT here carries no birth date, sex, referring physician, accession number or operator,
        /// so a writer that only copies what the reference happens to have drops Type 2 elements.
        /// </summary>
        [Theory]
        [MemberData(nameof(ReferenceModes))]
        public void NiftiToRtDose_WritesTheRtDoseIodRequiredAttributes(bool withReferenceSeries)
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                string folder = Path.Combine(root, "job");
                Directory.CreateDirectory(Path.Combine(folder, "doses"));
                using (var img = new Image(4, 3, 2, PixelIDValueEnum.sitkFloat32))
                {
                    img.SetSpacing(new VectorDouble(new[] { 2.5, 2.0, 3.0 }));
                    img.SetOrigin(new VectorDouble(new[] { -5.0, 4.0, 1.5 }));
                    for (uint z = 0; z < 2; z++)
                        for (uint y = 0; y < 3; y++)
                            for (uint x = 0; x < 4; x++)
                                img.SetPixelAsFloat(new VectorUInt32(new[] { x, y, z }), x + 4 * y + 12 * z + 0.25f);
                    NiftiConversionService.WriteImageSafely(img, Path.Combine(folder, "doses", "predicted.nii.gz"));
                }

                DicomSeriesGroup reference = null;
                NiftiPatientMetadata metadata = null;
                if (withReferenceSeries)
                {
                    string ctDir = DicomTestData.WriteCtSeriesWithPixels(
                        Path.Combine(root, "ct"), DicomTestData.NewUid(), DicomTestData.NewUid(), sliceCount: 2);
                    reference = new DicomSeriesGroup
                    {
                        Modality = "CT",
                        FilePaths = Directory.GetFiles(ctDir, "*.dcm").OrderBy(p => p, StringComparer.Ordinal).ToList(),
                    };
                }
                else
                {
                    metadata = new NiftiMetadataService().LoadOrSynthesize(folder);
                }

                var written = new RtDoseWriterService().ConvertDoseFolderToRtDoses(
                    folder, reference, null, CancellationToken.None, metadata: metadata);
                var file = DicomFile.Open(Assert.Single(written));
                var ds = file.Dataset;

                var missing = new List<string>();
                void Type1(DicomTag tag, string module)
                {
                    // Type 1: present with a value, i.e. a non-zero value length.
                    var element = ds.Contains(tag) ? ds.GetDicomItem<DicomElement>(tag) : null;
                    if (element == null || element.Buffer.Size == 0)
                        missing.Add($"{module}: {tag.DictionaryEntry.Keyword} (Type 1)");
                }
                void Type2(DicomTag tag, string module)
                {
                    if (!ds.Contains(tag))
                        missing.Add($"{module}: {tag.DictionaryEntry.Keyword} (Type 2)");
                }

                // Patient (C.7.1.1)
                Type2(DicomTag.PatientName, "Patient");
                Type2(DicomTag.PatientID, "Patient");
                Type2(DicomTag.PatientBirthDate, "Patient");
                Type2(DicomTag.PatientSex, "Patient");
                // General Study (C.7.2.1)
                Type1(DicomTag.StudyInstanceUID, "General Study");
                Type2(DicomTag.StudyDate, "General Study");
                Type2(DicomTag.StudyTime, "General Study");
                Type2(DicomTag.ReferringPhysicianName, "General Study");
                Type2(DicomTag.StudyID, "General Study");
                Type2(DicomTag.AccessionNumber, "General Study");
                // RT Series (C.8.8.1)
                Type1(DicomTag.Modality, "RT Series");
                Type1(DicomTag.SeriesInstanceUID, "RT Series");
                Type2(DicomTag.SeriesNumber, "RT Series");
                Type2(DicomTag.OperatorsName, "RT Series");
                // Frame of Reference (C.7.4.1)
                Type1(DicomTag.FrameOfReferenceUID, "Frame of Reference");
                Type2(DicomTag.PositionReferenceIndicator, "Frame of Reference");
                // General Equipment (C.7.5.1)
                Type2(DicomTag.Manufacturer, "General Equipment");
                // General Image (C.7.6.1)
                Type2(DicomTag.InstanceNumber, "General Image");
                // Image Plane (C.7.6.2)
                Type1(DicomTag.PixelSpacing, "Image Plane");
                Type1(DicomTag.ImageOrientationPatient, "Image Plane");
                Type1(DicomTag.ImagePositionPatient, "Image Plane");
                Type2(DicomTag.SliceThickness, "Image Plane");
                // Image Pixel (C.7.6.3)
                Type1(DicomTag.SamplesPerPixel, "Image Pixel");
                Type1(DicomTag.PhotometricInterpretation, "Image Pixel");
                Type1(DicomTag.Rows, "Image Pixel");
                Type1(DicomTag.Columns, "Image Pixel");
                Type1(DicomTag.BitsAllocated, "Image Pixel");
                Type1(DicomTag.BitsStored, "Image Pixel");
                Type1(DicomTag.HighBit, "Image Pixel");
                Type1(DicomTag.PixelRepresentation, "Image Pixel");
                if (!ds.Contains(DicomTag.PixelData)) missing.Add("Image Pixel: PixelData (Type 1C)");
                // Multi-frame (C.7.6.6)
                Type1(DicomTag.NumberOfFrames, "Multi-frame");
                Type1(DicomTag.FrameIncrementPointer, "Multi-frame");
                // RT Dose (C.8.8.3)
                Type1(DicomTag.DoseUnits, "RT Dose");
                Type1(DicomTag.DoseType, "RT Dose");
                Type1(DicomTag.DoseSummationType, "RT Dose");
                Type1(DicomTag.GridFrameOffsetVector, "RT Dose (1C: multi-frame)");
                Type1(DicomTag.DoseGridScaling, "RT Dose (1C: pixel data present)");
                // SOP Common (C.12.1)
                Type1(DicomTag.SOPClassUID, "SOP Common");
                Type1(DicomTag.SOPInstanceUID, "SOP Common");

                Assert.True(missing.Count == 0, "RT-DOSE is missing IOD-required attributes:\n  " + string.Join("\n  ", missing));

                // The values the RT Dose module constrains.
                Assert.Equal(DicomUID.RTDoseStorage.UID, ds.GetString(DicomTag.SOPClassUID));
                Assert.Equal("RTDOSE", ds.GetString(DicomTag.Modality));
                Assert.Equal((ushort)1, ds.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
                Assert.Equal("MONOCHROME2", ds.GetString(DicomTag.PhotometricInterpretation));
                ushort bits = ds.GetSingleValue<ushort>(DicomTag.BitsAllocated);
                Assert.Contains(bits, new ushort[] { 16, 32 });
                Assert.Equal(bits, ds.GetSingleValue<ushort>(DicomTag.BitsStored));
                Assert.Equal(bits - 1, ds.GetSingleValue<ushort>(DicomTag.HighBit));
                Assert.Equal((ushort)0, ds.GetSingleValue<ushort>(DicomTag.PixelRepresentation));
                Assert.Equal(DicomTag.GridFrameOffsetVector, ds.GetSingleValue<DicomTag>(DicomTag.FrameIncrementPointer));
                int frames = int.Parse(ds.GetString(DicomTag.NumberOfFrames), CultureInfo.InvariantCulture);
                double[] gfov = ds.GetValues<double>(DicomTag.GridFrameOffsetVector);
                Assert.Equal(2, frames);
                Assert.Equal(frames, gfov.Length);
                Assert.Equal(0.0, gfov[0]);
                Assert.Equal(6, ds.GetValues<string>(DicomTag.ImageOrientationPatient).Length);
                Assert.Equal(3, ds.GetValues<string>(DicomTag.ImagePositionPatient).Length);
                Assert.Equal(2, ds.GetValues<string>(DicomTag.PixelSpacing).Length);
                int rows = ds.GetSingleValue<ushort>(DicomTag.Rows);
                int cols = ds.GetSingleValue<ushort>(DicomTag.Columns);
                Assert.Equal(rows * cols * frames * (bits / 8),
                    ds.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Size);
                Assert.Contains(ds.GetString(DicomTag.DoseType), new[] { "PHYSICAL", "EFFECTIVE", "ERROR" });
                Assert.Contains(ds.GetString(DicomTag.DoseUnits), new[] { "GY", "RELATIVE" });

                // Part 10 file meta matches the dataset.
                Assert.Equal(ds.GetString(DicomTag.SOPClassUID),
                    file.FileMetaInfo.MediaStorageSOPClassUID.UID);
                Assert.Equal(ds.GetString(DicomTag.SOPInstanceUID),
                    file.FileMetaInfo.MediaStorageSOPInstanceUID.UID);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The one RT Dose IOD condition the writer cannot meet on its own: Referenced RT Plan
        /// Sequence is Type 1C, required for Dose Summation Type PLAN (and every other defined
        /// term needs a plan or treatment-record reference), and a dose born from a NIfTI has no
        /// plan. Skipped, not deleted, so the gap stays visible in the test counts until the
        /// decision in the linked issue lands.
        /// </summary>
        [Fact(Skip = "Known RT Dose IOD gap, needs a decision: toolkit issue #56")]
        public void NiftiToRtDose_PlanSummation_CarriesReferencedRtPlanSequence()
        {
            string folder = DicomTestData.NewTempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "doses"));
                using (var img = new Image(2, 2, 2, PixelIDValueEnum.sitkFloat32))
                {
                    img.SetPixelAsFloat(new VectorUInt32(new uint[] { 1, 1, 1 }), 2.0f);
                    NiftiConversionService.WriteImageSafely(img, Path.Combine(folder, "doses", "d.nii.gz"));
                }
                var metadata = new NiftiMetadataService().LoadOrSynthesize(folder);
                var ds = DicomFile.Open(Assert.Single(new RtDoseWriterService().ConvertDoseFolderToRtDoses(
                    folder, null, null, CancellationToken.None, metadata: metadata))).Dataset;

                Assert.Equal("PLAN", ds.GetString(DicomTag.DoseSummationType));
                Assert.True(ds.Contains(DicomTag.ReferencedRTPlanSequence),
                    "Dose Summation Type PLAN requires Referenced RT Plan Sequence (300C,0002)");
            }
            finally
            {
                TryDelete(folder);
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────

        private static DicomSeriesGroup WriteDoseSeries(
            string root, DoseGrid grid, uint[] stored, int bitsAllocated, double scaling, string units)
        {
            string dir = Path.Combine(root, "in");
            string path = SyntheticRtDose.Write(dir, "dose.dcm", grid, stored, bitsAllocated, scaling, units,
                "DOSE01", DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(),
                seriesDescription: "Plan dose");
            return new DicomSeriesGroup
            {
                Modality = "RTDOSE",
                SeriesDescription = "Plan dose",
                FilePaths = new List<string> { path },
            };
        }

        private static string Forward(DicomSeriesGroup dose, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            string written = new NiftiConversionService(new RtStructMaskService()).ConvertDoseToNifti(
                dose, outputDir, null, CancellationToken.None);
            Assert.NotNull(written);
            Assert.True(File.Exists(written), written);
            return written;
        }

        private static void AssertGrid(DoseGrid grid, Image img)
        {
            Assert.Equal(new[] { (uint)grid.Columns, (uint)grid.Rows, (uint)grid.Frames }, img.GetSize().ToArray());
            var spacing = img.GetSpacing();
            Assert.Equal(grid.ColumnSpacing, spacing[0], 6);
            Assert.Equal(grid.RowSpacing, spacing[1], 6);
            Assert.Equal(grid.FrameSpacing, spacing[2], 6);
            var origin = img.GetOrigin();
            for (int d = 0; d < 3; d++)
                Assert.Equal(grid.Origin[d], origin[d], 6);
            var direction = img.GetDirection();
            double[] expected = grid.ItkDirection;
            for (int i = 0; i < 9; i++)
                Assert.True(Math.Abs(expected[i] - direction[i]) < 1e-6,
                    $"direction[{i}] is {direction[i]}, expected {expected[i]} " +
                    $"(got [{string.Join(",", direction)}])");
        }

        private static void AssertRtDoseGrid(DoseGrid grid, DicomDataset ds)
        {
            Assert.Equal(grid.Rows, ds.GetSingleValue<ushort>(DicomTag.Rows));
            Assert.Equal(grid.Columns, ds.GetSingleValue<ushort>(DicomTag.Columns));
            Assert.Equal(grid.Frames, int.Parse(ds.GetString(DicomTag.NumberOfFrames), CultureInfo.InvariantCulture));

            double[] ps = ds.GetValues<double>(DicomTag.PixelSpacing);
            Assert.Equal(grid.RowSpacing, ps[0], 6);
            Assert.Equal(grid.ColumnSpacing, ps[1], 6);

            double[] ipp = ds.GetValues<double>(DicomTag.ImagePositionPatient);
            for (int d = 0; d < 3; d++)
                Assert.Equal(grid.Origin[d], ipp[d], 6);

            double[] iop = ds.GetValues<double>(DicomTag.ImageOrientationPatient);
            for (int i = 0; i < 6; i++)
                Assert.True(Math.Abs(grid.Iop[i] - iop[i]) < 1e-6,
                    $"IOP[{i}] is {iop[i]}, expected {grid.Iop[i]}");

            double[] gfov = ds.GetValues<double>(DicomTag.GridFrameOffsetVector);
            Assert.Equal(grid.Frames, gfov.Length);
            for (int k = 0; k < grid.Frames; k++)
                Assert.Equal(k * grid.FrameSpacing, gfov[k], 6);
        }

        private static double[] ReadDoubles(Image img)
        {
            using (var asDouble = SimpleITK.Cast(img, PixelIDValueEnum.sitkFloat64))
            {
                int n = (int)(asDouble.GetNumberOfPixels());
                var values = new double[n];
                Marshal.Copy(asDouble.GetBufferAsDouble(), values, 0, n);
                return values;
            }
        }

        private static uint[] ReadStoredUInt32(DicomDataset ds)
        {
            Assert.Equal((ushort)32, ds.GetSingleValue<ushort>(DicomTag.BitsAllocated));
            byte[] bytes = ds.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            var values = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
            return values;
        }

        private static double MaxAbsError(double[] values, uint[] stored, double scaling)
        {
            Assert.Equal(stored.Length, values.Length);
            double maxErr = 0;
            for (int i = 0; i < values.Length; i++)
                maxErr = Math.Max(maxErr, Math.Abs(values[i] - stored[i] * scaling));
            return maxErr;
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
