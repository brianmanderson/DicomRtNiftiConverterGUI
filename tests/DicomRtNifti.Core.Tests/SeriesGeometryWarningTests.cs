using System.Collections.Generic;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// SeriesGeometryProbe could always tell that a series had mixed slice gaps, but nothing on
    /// the forward conversion paths asked it. A CT reconstructed at 0/3/6/50/53/56 mm therefore
    /// exported at the endpoint-average 11.2 mm with exit code 0 and no message naming either the
    /// gaps or the spacing that had just been written — and every volume derived from that NIfTI
    /// was out by 11.2/3 ≈ 3.7x.
    ///
    /// These cover the warning itself: that it fires on exactly that geometry, names the numbers a
    /// reader needs to act, and stays quiet on a regular grid.
    /// </summary>
    public class SeriesGeometryWarningTests
    {
        /// <summary>The repro geometry: two 3 mm blocks separated by a 44 mm jump.</summary>
        private static DicomSeriesGroup NonUniformSeries()
        {
            return new DicomSeriesGroup
            {
                SeriesInstanceUID = "1.2.826.0.1.3680043.8.498.99",
                Modality = "CT",
                FilePaths = new List<string> { "a", "b", "c", "d", "e", "f" },
                SlicePositions = new List<double> { 0, 3, 6, 50, 53, 56 },
                PixelSpacing = new[] { 1.0, 1.0 },
                SliceThickness = 3.0,
            };
        }

        [Fact]
        public void Warns_OnMixedSliceGaps_AndNamesGapsAndWrittenSpacing()
        {
            string warning;
            bool warned = SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(
                NonUniformSeries(), out warning);

            Assert.True(warned);
            Assert.Contains("1.2.826.0.1.3680043.8.498.99", warning);
            Assert.Contains("3 mm", warning);      // smallest gap
            Assert.Contains("44 mm", warning);     // largest gap
            Assert.Contains("11.2 mm", warning);   // what actually gets written: (56-0)/5
        }

        [Fact]
        public void DoesNotWarn_OnAUniformGrid()
        {
            var series = new DicomSeriesGroup
            {
                SeriesInstanceUID = "1.2.3",
                Modality = "CT",
                FilePaths = new List<string> { "a", "b", "c", "d" },
                SlicePositions = new List<double> { 0, 2.5, 5, 7.5 },
                PixelSpacing = new[] { 1.0, 1.0 },
            };

            string warning;
            Assert.False(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(series, out warning));
            Assert.Null(warning);
        }

        /// <summary>
        /// An RTSTRUCT/RTDOSE series and a single-slice image series have no gaps to judge, and a
        /// warning there would be noise on every run.
        /// </summary>
        [Fact]
        public void DoesNotWarn_WhenThereIsNoGapToJudge()
        {
            string warning;

            var noPositions = new DicomSeriesGroup { Modality = "RTSTRUCT", FilePaths = new List<string> { "s" } };
            Assert.False(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(noPositions, out warning));

            var single = new DicomSeriesGroup
            {
                Modality = "CT",
                FilePaths = new List<string> { "a" },
                SlicePositions = new List<double> { 4.0 },
                SliceThickness = 3.0,
            };
            Assert.False(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(single, out warning));
        }

        /// <summary>
        /// The written spacing is the endpoint average, not the median the probe reports: that is
        /// what ImageSeriesReader puts in the NIfTI header, so quoting the median would understate
        /// the error by exactly the factor the warning exists to flag.
        /// </summary>
        [Fact]
        public void QuotesTheEndpointAverage_NotTheMedianGap()
        {
            SeriesGeometry geometry;
            Assert.True(SeriesGeometryProbe.TryDescribe(NonUniformSeries(), out geometry));
            Assert.Equal(3.0, geometry.SliceSpacing.Value, 6);   // median gap

            string warning;
            SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(NonUniformSeries(), out warning);
            Assert.DoesNotContain("written at 3 mm", warning);
            Assert.Contains("11.2 mm", warning);
        }
    }
}
