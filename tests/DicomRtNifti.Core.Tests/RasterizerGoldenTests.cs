using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Golden values for the rasterizer (D-9). The analytic gate measures accuracy against
    /// closed-form shapes with tolerances; nothing there pins an exact voxel count or where the
    /// voxels sit, so a tie-rule change, a one-voxel shift, or a flipped axis could stay green.
    /// These tests do: exact counts, exact occupied ranges on an off-centre square (a flip moves
    /// rows 2..11 to 4..13 on this 16-voxel grid while leaving the count alone), the mask's
    /// direction against the reference's, and a written mask's header read back with SimpleITK.
    ///
    /// Grid: 1 mm, origin (0,0,0), identity direction, 16x16x4, so a physical coordinate in mm
    /// equals its continuous voxel index. Scanline convention under test (RtStructMaskService):
    /// a row is crossed when yi &lt;= y &lt; yj (half-open), and the X fill runs from ceil(x0) to
    /// floor(x1) inclusive (closed at both ends).
    /// </summary>
    public class RasterizerGoldenTests
    {
        private const int Cols = 16, Rows = 16, Slices = 4;
        private const string Roi = "sq";

        private struct SliceStats
        {
            public int Count, MinX, MaxX, MinY, MaxY;
        }

        private static Image MakeAxialReference(double[] direction = null)
        {
            var img = new Image((uint)Cols, (uint)Rows, (uint)Slices, PixelIDValueEnum.sitkInt16);
            img.SetOrigin(new VectorDouble(new[] { 0.0, 0.0, 0.0 }));
            img.SetSpacing(new VectorDouble(new[] { 1.0, 1.0, 1.0 }));
            if (direction != null)
                img.SetDirection(new VectorDouble(direction));
            return img;
        }

        private static byte[] Buffer(Image mask)
        {
            var size = mask.GetSize();
            var buf = new byte[size[0] * size[1] * size[2]];
            Marshal.Copy(mask.GetBufferAsUInt8(), buf, 0, buf.Length);
            return buf;
        }

        private static SliceStats Stats(Image mask, int z)
        {
            var size = mask.GetSize();
            int cols = (int)size[0], rows = (int)size[1];
            var buf = Buffer(mask);
            var s = new SliceStats { MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1 };
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    if (buf[z * rows * cols + y * cols + x] != 0)
                    {
                        s.Count++;
                        s.MinX = Math.Min(s.MinX, x); s.MaxX = Math.Max(s.MaxX, x);
                        s.MinY = Math.Min(s.MinY, y); s.MaxY = Math.Max(s.MaxY, y);
                    }
            return s;
        }

        private static int[] VoxelsPerSlice(Image mask)
        {
            var size = mask.GetSize();
            int cols = (int)size[0], rows = (int)size[1], slices = (int)size[2];
            var buf = Buffer(mask);
            var perSlice = new int[slices];
            for (int z = 0; z < slices; z++)
                for (int i = 0; i < rows * cols; i++)
                    if (buf[z * rows * cols + i] != 0) perSlice[z]++;
            return perSlice;
        }

        private static string WriteSquareRtStruct(string dir, double[] contour)
        {
            return DicomTestData.WriteRtStructWithContours(dir, "rs.dcm",
                DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(),
                new List<KeyValuePair<string, double[]>> { new KeyValuePair<string, double[]>(Roi, contour) });
        }

        private static Image Rasterize(string rtStructPath, Image reference)
        {
            var masks = new RtStructMaskService().RasterizeRois(
                rtStructPath, reference,
                new Dictionary<string, string> { { Roi, Roi } },
                progress: null, ct: CancellationToken.None);
            return masks[Roi];
        }

        private static void AssertSquare(SliceStats s, int count, int minX, int maxX, int minY, int maxY)
        {
            Assert.Equal(count, s.Count);
            Assert.Equal(minX, s.MinX);
            Assert.Equal(maxX, s.MaxX);
            Assert.Equal(minY, s.MinY);
            Assert.Equal(maxY, s.MaxY);
        }

        [Fact]
        public void ClosedPlanar_SquareOnVoxelBoundaries_FillsExactlyNbyN()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                // Corners at 1.5 and 11.5 mm sit on voxel boundaries: rows 2..11 are crossed
                // (1.5 <= y < 11.5), columns ceil(1.5)=2 .. floor(11.5)=11. 10 x 10 = 100.
                string rs = WriteSquareRtStruct(dir, DicomTestData.SquareContour(1.5, 11.5, 1.0));
                using (var mask = Rasterize(rs, MakeAxialReference()))
                {
                    AssertSquare(Stats(mask, 1), 100, 2, 11, 2, 11);
                    Assert.Equal(new[] { 0, 100, 0, 0 }, VoxelsPerSlice(mask));
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ClosedPlanar_SquareOnVoxelCentres_RasterizesElevenByTen()
        {
            // S25, see W3-E: the X fill is closed at both ends (ceil(2)=2 .. floor(12)=12, eleven
            // columns) while the Y crossing is half-open (2 <= y < 12, ten rows), so a square
            // whose corners sit on voxel centres rasterizes 11 x 10. Pinned here, not fixed
            // (D-9). If W3-E aligns the two rules this becomes 10 x 10 and this test is updated
            // in the same change.
            string dir = DicomTestData.NewTempDir();
            try
            {
                string rs = WriteSquareRtStruct(dir, DicomTestData.SquareContour(2.0, 12.0, 1.0));
                using (var mask = Rasterize(rs, MakeAxialReference()))
                {
                    AssertSquare(Stats(mask, 1), 110, 2, 12, 2, 11);
                    Assert.Equal(new[] { 0, 110, 0, 0 }, VoxelsPerSlice(mask));
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Mask_CopiesTheReferenceDirectionExactly()
        {
            // An in-plane 3-4-5 rotation about Z: exact decimals, slice normal still +Z, so the
            // per-slice z lookup is unaffected. Physical corners of the boundary square under
            // p = (0.6 i - 0.8 j, 0.8 i + 0.6 j, z).
            double[] direction = { 0.6, -0.8, 0.0, 0.8, 0.6, 0.0, 0.0, 0.0, 1.0 };
            double[] contour =
            {
                -0.3, 2.1, 1.0,
                5.7, 10.1, 1.0,
                -2.3, 16.1, 1.0,
                -8.3, 8.1, 1.0,
            };
            string dir = DicomTestData.NewTempDir();
            try
            {
                string rs = WriteSquareRtStruct(dir, contour);
                using (var reference = MakeAxialReference(direction))
                using (var mask = Rasterize(rs, reference))
                {
                    Assert.Equal(reference.GetDirection().ToArray(), mask.GetDirection().ToArray());
                    Assert.Equal(reference.GetOrigin().ToArray(), mask.GetOrigin().ToArray());
                    Assert.Equal(reference.GetSpacing().ToArray(), mask.GetSpacing().ToArray());
                    AssertSquare(Stats(mask, 1), 100, 2, 11, 2, 11);
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void WrittenMask_HeaderMatchesTheReferenceSeries_Axial()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                var (roiVolumes, maskPath) = ConvertSquare(dir, null, DicomTestData.SquareContour(1.5, 11.5, 1.0));

                Assert.Equal(100.0 / 1000.0, roiVolumes[Roi], 9);
                using (Image back = SimpleITK.ReadImage(maskPath))
                {
                    Assert.Equal(PixelIDValueEnum.sitkUInt8, back.GetPixelID());
                    Assert.Equal(new uint[] { Cols, Rows, Slices }, back.GetSize().ToArray());
                    AssertClose(new[] { 1.0, 1.0, 1.0 }, back.GetSpacing().ToArray());
                    AssertClose(new[] { 0.0, 0.0, 0.0 }, back.GetOrigin().ToArray());
                    AssertClose(new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 }, back.GetDirection().ToArray());
                    AssertSquare(Stats(back, 1), 100, 2, 11, 2, 11);
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void WrittenMask_HeaderMatchesTheReferenceSeries_Oblique()
        {
            double[] iop = { 0.6, 0.8, 0.0, -0.8, 0.6, 0.0 };
            double[] expectedDirection = { 0.6, -0.8, 0.0, 0.8, 0.6, 0.0, 0.0, 0.0, 1.0 };
            double[] contour =
            {
                -0.3, 2.1, 1.0,
                5.7, 10.1, 1.0,
                -2.3, 16.1, 1.0,
                -8.3, 8.1, 1.0,
            };
            string dir = DicomTestData.NewTempDir();
            try
            {
                var (roiVolumes, maskPath) = ConvertSquare(dir, iop, contour);

                Assert.Equal(100.0 / 1000.0, roiVolumes[Roi], 9);
                using (Image back = SimpleITK.ReadImage(maskPath))
                {
                    AssertClose(expectedDirection, back.GetDirection().ToArray());
                    AssertClose(new[] { 1.0, 1.0, 1.0 }, back.GetSpacing().ToArray());
                    AssertClose(new[] { 0.0, 0.0, 0.0 }, back.GetOrigin().ToArray());
                    AssertSquare(Stats(back, 1), 100, 2, 11, 2, 11);
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>The end-to-end route: a real CT series on disk, loaded, rasterized, written.</summary>
        private static (Dictionary<string, double>, string) ConvertSquare(string dir, double[] iop, double[] contour)
        {
            string ctDir = Path.Combine(dir, "ct");
            string studyUid = DicomTestData.NewUid();
            string ctUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, ctUid, frameUid, sliceCount: Slices, size: Cols,
                studyUid: studyUid, imageOrientationPatient: iop);
            string rs = DicomTestData.WriteRtStructWithContours(dir, "rs.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>> { new KeyValuePair<string, double[]>(Roi, contour) },
                referencedSeriesUid: ctUid);

            var image = Cli.HeadlessRunner.BuildImageSeriesFromFolder(ctDir);
            var rtStruct = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { rs },
                RoiNames = new List<string> { Roi },
            };

            // flatOutput writes straight into outputDir and expects it to exist (the CLI creates it).
            string outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            var volumes = new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                rtStruct, image, outDir, associations: null, exportUnmatched: true, flatOutput: true,
                progress: null, ct: CancellationToken.None);
            return (volumes, Path.Combine(outDir, Roi + ".nii.gz"));
        }

        private static void AssertClose(double[] expected, double[] actual, double tolerance = 1e-6)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                    $"index {i}: expected {expected[i]} but was {actual[i]} ([{string.Join(", ", actual)}])");
        }
    }
}
