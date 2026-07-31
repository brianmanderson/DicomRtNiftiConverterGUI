using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// ScanlineFillPolygon clamps its loop bounds to the grid, so a contour at coordinates that
    /// have nothing to do with the reference series was not rejected by the clamp — it was
    /// accepted, and filled whatever the clamped range covered. At ±1e9 mm the polygon spans the
    /// whole slice and every voxel in it came out set; at 1e12 mm the unchecked double→int cast
    /// left int's range and the mask came out empty instead. Both reported success, and both are
    /// the signature of an RTSTRUCT matched to the wrong image series.
    ///
    /// The fix rejects such contours into the existing "unhandled" bucket, whose diagnostic already
    /// names that cause. These pin both ends: the absurd contour yields no mask at all, and a
    /// contour that merely spills past the field of view still rasterizes.
    ///
    /// Volumes are read from the conversion's own return value (cc), so the whole grid here is
    /// 16 x 16 x 4 voxels of 1 mm = 1.024 cc and one filled slice is 0.256 cc.
    /// </summary>
    public class ContourBoundsTests
    {
        private const int GridSize = 16;
        private const int SliceCount = 4;
        private const double FullSliceCc = GridSize * GridSize / 1000.0;

        [Theory]
        [InlineData(1e9)]    // spans the grid through the clamp: used to fill the entire slice
        [InlineData(1e12)]   // overflowed the double->int cast: used to yield an all-zero mask
        [InlineData(1e6)]
        public void ContourFarOutsideTheGrid_ProducesNoMask(double magnitude)
        {
            var volumes = Rasterize("far_out", new[]
            {
                -magnitude, -magnitude, 1.0,
                 magnitude, -magnitude, 1.0,
                 magnitude,  magnitude, 1.0,
                -magnitude,  magnitude, 1.0,
            });

            // Nothing rasterized, so the ROI is dropped with the "0 of N contours" diagnostic
            // instead of emitting a mask that is entirely set or entirely clear.
            Assert.False(volumes.ContainsKey("far_out"));
        }

        [Fact]
        public void ContourOnTheGrid_StillRasterizesToItsOwnArea()
        {
            var volumes = Rasterize("normal", DicomTestData.SquareContour(4, 10, 1.0));

            Assert.True(volumes.ContainsKey("normal"));
            Assert.True(volumes["normal"] > 0, "a 6 x 6 mm square must fill something");
            Assert.True(volumes["normal"] < FullSliceCc, "and must not fill the whole slice");
        }

        /// <summary>
        /// The allowance is deliberately generous. Contours routinely extend past the reconstructed
        /// field of view — a body outline clipped by the FOV, couch and immobilisation structures —
        /// and dropping those would lose data the converter handles correctly today. A contour
        /// reaching several voxels beyond the edge must still fill its in-grid part.
        /// </summary>
        [Fact]
        public void ContourSpillingPastTheFieldOfView_StillRasterizes()
        {
            var volumes = Rasterize("overhang", DicomTestData.SquareContour(-6, GridSize + 5, 1.0));

            Assert.True(volumes.ContainsKey("overhang"));
            Assert.Equal(FullSliceCc, volumes["overhang"], 6);
        }

        // ---------- helpers ----------

        /// <summary>
        /// Runs one ROI through the real conversion path and returns its ROI name -> volume (cc).
        /// </summary>
        private static Dictionary<string, double> Rasterize(string roiName, double[] contourData)
        {
            string root = DicomTestData.NewTempDir();
            string ctDir = Path.Combine(root, "ct");

            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();

            DicomTestData.WriteCtSeriesWithPixels(
                ctDir, seriesUid, frameUid, sliceCount: SliceCount, size: GridSize, studyUid: studyUid);

            string rtstructPath = DicomTestData.WriteRtStructWithContours(
                root, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>(roiName, contourData),
                });

            var imageSeries = new DicomSeriesGroup
            {
                SeriesInstanceUID = seriesUid,
                Modality = "CT",
                FrameOfReferenceUID = frameUid,
                FilePaths = Directory.GetFiles(ctDir, "*.dcm")
                                     .OrderBy(p => p, StringComparer.Ordinal)
                                     .ToList(),
            };
            var structSeries = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { rtstructPath },
            };
            structSeries.RoiNames.Add(roiName);

            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(outDir);

            return new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                structSeries, imageSeries, outDir,
                associations: null, exportUnmatched: true, flatOutput: true,
                progress: null, ct: CancellationToken.None);
        }
    }
}
