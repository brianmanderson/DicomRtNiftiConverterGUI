using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Z-placement tests for the CLOSED_NONPLANAR rasterization path.
    ///
    /// <para>
    /// <c>RasterizeClosedNonplanar</c> slices a 3D polygon at each axial plane it
    /// spans. The plane for slice <c>sz</c> must be the slice CENTRE. Contour points
    /// arrive as continuous voxel indices from
    /// <c>TransformPhysicalPointToContinuousIndex</c>, which puts the centre of slice
    /// <c>i</c> at index <c>i.0</c> — so the centre is <c>sz</c>, and <c>sz + 0.5</c>
    /// is the boundary between slices <c>sz</c> and <c>sz+1</c>. Sampling at the
    /// boundary and then storing the result at <c>sz</c> displaces every
    /// CLOSED_NONPLANAR mask half a slice toward −Z.
    /// </para>
    ///
    /// <para>
    /// Neither the conformance fixture nor the parent repo's synthetic benchmark
    /// dataset contains a CLOSED_NONPLANAR contour (they cover CLOSED_PLANAR,
    /// OPEN_NONPLANAR and POINT), so this path has no other coverage — hence these
    /// tests. They assert only on which SLICES are occupied, never on the in-plane
    /// shape, so they are independent of the X/Y scanline-fill convention.
    /// </para>
    /// </summary>
    public class RtStructNonplanarSliceTests
    {
        // Reference grid: 2 mm in-plane, 3 mm slices, origin at the centre of voxel
        // (0,0,0). Voxel (x,y,z) therefore has its centre at (2x, 2y, 3z) mm.
        private const uint Cols = 64, Rows = 64, Slices = 24;
        private const double DxMm = 2.0, DyMm = 2.0, DzMm = 3.0;

        private static Image MakeReferenceImage()
        {
            var img = new Image(Cols, Rows, Slices, PixelIDValueEnum.sitkInt16);
            img.SetOrigin(new VectorDouble(new[] { 0.0, 0.0, 0.0 }));
            img.SetSpacing(new VectorDouble(new[] { DxMm, DyMm, DzMm }));
            return img;
        }

        /// <summary>
        /// Writes a single-ROI RTSTRUCT whose one contour carries the given geometric
        /// type and points (flat [x0,y0,z0, x1,y1,z1, ...] in mm). Returns the path.
        /// </summary>
        private static string WriteContourRtStruct(
            string dir, string roiName, string geometricType, double[] pointsXyzMm)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, "NONPLANAR" },
                { DicomTag.Modality, "RTSTRUCT" },
                { DicomTag.StructureSetLabel, "nonplanar" },
            };

            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence,
                new DicomDataset
                {
                    { DicomTag.ROINumber, "1" },
                    { DicomTag.ROIName, roiName },
                }));

            var contour = new DicomDataset
            {
                { DicomTag.ContourGeometricType, geometricType },
                {
                    DicomTag.NumberOfContourPoints,
                    (pointsXyzMm.Length / 3).ToString(CultureInfo.InvariantCulture)
                },
            };
            // ContourData is VR=DS, capped at 16 characters per value, so a
            // round-trip format like G17 is rejected by fo-dicom's validator.
            // Six decimals of a millimetre is far finer than any voxel here.
            contour.Add(DicomTag.ContourData,
                pointsXyzMm.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)).ToArray());

            var roiContour = new DicomDataset { { DicomTag.ReferencedROINumber, "1" } };
            roiContour.Add(new DicomSequence(DicomTag.ContourSequence, contour));
            ds.Add(new DicomSequence(DicomTag.ROIContourSequence, roiContour));

            string path = Path.Combine(dir, "nonplanar_rtstruct.dcm");
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>Voxel count per slice index, read straight out of the mask buffer.</summary>
        private static int[] VoxelsPerSlice(Image mask)
        {
            var size = mask.GetSize();
            int cols = (int)size[0], rows = (int)size[1], slices = (int)size[2];
            var buf = new byte[cols * rows * slices];
            Marshal.Copy(mask.GetBufferAsUInt8(), buf, 0, buf.Length);

            var perSlice = new int[slices];
            for (int z = 0; z < slices; z++)
            {
                int offset = z * rows * cols, n = 0;
                for (int i = 0; i < rows * cols; i++) if (buf[offset + i] != 0) n++;
                perSlice[z] = n;
            }
            return perSlice;
        }

        private static Dictionary<string, Image> Rasterize(string rtStructPath, string roiName)
        {
            return new RtStructMaskService().RasterizeRois(
                rtStructPath,
                MakeReferenceImage(),
                new Dictionary<string, string> { { roiName, roiName } },
                progress: null,
                ct: CancellationToken.None);
        }

        /// <summary>
        /// A closed loop lying flat on the centre of slice 10, declared
        /// CLOSED_NONPLANAR — exactly what a zero-tilt tilted-loop primitive produces.
        ///
        /// Every edge has z0 == z1, so no edge ever "crosses" the sampling plane; the
        /// contour is only picked up by the vertex-on-plane branch
        /// (|z − planeZ| &lt; 0.001). Sampling at the slice centre hits it exactly.
        /// Sampling at sz + 0.5 misses by half a slice for every sz, the polygon is
        /// dropped, and the ROI silently produces no mask at all.
        /// </summary>
        [Fact]
        public void ClosedNonplanar_FlatLoopOnSliceCentre_FillsExactlyThatSlice()
        {
            const int expectedSlice = 10;
            double zMm = expectedSlice * DzMm; // centre of slice 10

            // Axis-aligned square, indices 20..40 in x and y.
            var pts = new List<double>();
            foreach (var (ix, iy) in new[] { (20, 20), (40, 20), (40, 40), (20, 40) })
            {
                pts.Add(ix * DxMm); pts.Add(iy * DyMm); pts.Add(zMm);
            }

            string dir = DicomTestData.NewTempDir();
            try
            {
                string rt = WriteContourRtStruct(dir, "loop", "CLOSED_NONPLANAR", pts.ToArray());
                var masks = Rasterize(rt, "loop");

                Assert.True(masks.ContainsKey("loop"),
                    "CLOSED_NONPLANAR contour produced no mask at all — the sampling plane " +
                    "missed the contour's own slice, which is the sz + 0.5 (slice-boundary) bug.");

                int[] perSlice = VoxelsPerSlice(masks["loop"]);
                Assert.True(perSlice[expectedSlice] > 0,
                    $"slice {expectedSlice} is empty; occupied slices: " +
                    string.Join(",", Enumerable.Range(0, perSlice.Length).Where(z => perSlice[z] > 0)));
                for (int z = 0; z < perSlice.Length; z++)
                {
                    if (z == expectedSlice) continue;
                    Assert.True(perSlice[z] == 0, $"slice {z} should be empty but has {perSlice[z]} voxel(s)");
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// A circular loop tilted about the X axis so its Z extent is symmetric about
        /// the centre of slice 10, spanning slices 8..12. The voxel-count-weighted
        /// centroid of the occupied slices must therefore sit at 10.0.
        ///
        /// Sampling at sz + 0.5 shifts every cross-section half a slice, pulling the
        /// centroid to roughly 9.5 and dropping the topmost slice.
        /// </summary>
        [Fact]
        public void ClosedNonplanar_TiltedLoop_IsCentredOnItsZExtent()
        {
            const double centreSlice = 10.0;
            const int nSamples = 256;
            double zCentreMm = centreSlice * DzMm;   // 30 mm
            double radiusMm = 40.0;                  // 20 voxels in-plane
            double zAmplitudeMm = 2 * DzMm;          // +/- 2 slices => spans 8..12

            // Sample off-phase. With a whole-number phase the vertices land exactly on
            // slice planes (sin t hitting 0, +/-0.5, +/-1 at exact sample indices); each
            // such vertex is then counted both by the edge-crossing branch and by the
            // vertex-on-plane branch, producing duplicate cross-section points and a
            // degenerate polygon. That is a fixture artefact, not the behaviour under
            // test, and the offset keeps every vertex clear of an integer slice index.
            const double phase = 0.3;

            var pts = new List<double>();
            for (int i = 0; i < nSamples; i++)
            {
                double t = 2.0 * Math.PI * (i + phase) / nSamples;
                double s = Math.Sin(t);
                pts.Add(64.0 + radiusMm * Math.Cos(t));  // x, mm
                pts.Add(64.0 + radiusMm * s);            // y, mm
                pts.Add(zCentreMm + zAmplitudeMm * s);   // z, mm
            }

            string dir = DicomTestData.NewTempDir();
            try
            {
                string rt = WriteContourRtStruct(dir, "tilted", "CLOSED_NONPLANAR", pts.ToArray());
                var masks = Rasterize(rt, "tilted");
                Assert.True(masks.ContainsKey("tilted"), "tilted CLOSED_NONPLANAR loop produced no mask");

                int[] perSlice = VoxelsPerSlice(masks["tilted"]);
                long total = perSlice.Sum(v => (long)v);
                Assert.True(total > 0, "tilted loop rasterized to an empty mask");

                double weighted = 0;
                for (int z = 0; z < perSlice.Length; z++) weighted += (double)z * perSlice[z];
                double centroid = weighted / total;

                Assert.True(Math.Abs(centroid - centreSlice) < 0.25,
                    $"occupied-slice centroid {centroid:F3} is not centred on slice {centreSlice}; " +
                    $"a value near {centreSlice - 0.5:F1} is the sz + 0.5 half-slice bias. " +
                    "Per-slice counts: " +
                    string.Join(",", Enumerable.Range(0, perSlice.Length)
                        .Where(z => perSlice[z] > 0).Select(z => $"{z}:{perSlice[z]}")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
