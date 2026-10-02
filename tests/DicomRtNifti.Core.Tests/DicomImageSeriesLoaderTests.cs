using System;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The series loader on a mixed-gap series (S24). ITK's ImageSeriesReader sets the slice
    /// spacing to ‖IPP_last − IPP_first‖ / (N − 1) over the list it is given, so the loader's old
    /// guard, which compared exactly those two quantities, could never fire; this pins what the
    /// loader actually does with such a series so that a future SimpleITK that derives the
    /// spacing differently reopens the question here rather than in production.
    /// </summary>
    public class DicomImageSeriesLoaderTests
    {
        [Fact]
        public void LoadCorrected_MixedGaps_DoesNotThrow_SpacingIsTheEndpointAverage()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                // Gaps 1, 1, 3 mm: positions 0, 1, 2, 5.
                DicomTestData.WriteCtSeriesWithPixels(dir, DicomTestData.NewUid(), DicomTestData.NewUid(),
                    size: 8, sliceZsMm: new[] { 0.0, 1.0, 2.0, 5.0 });
                var files = Directory.GetFiles(dir, "*.dcm").OrderBy(f => f, StringComparer.Ordinal).ToList();

                using (Image img = DicomImageSeriesLoader.LoadCorrected(files))
                {
                    Assert.Equal(4u, img.GetSize()[2]);
                    // The endpoint average (5 / 3), not the first gap (1): the number any volume
                    // derived from the written spacing is scaled by, which is what the probe warns about.
                    Assert.Equal(5.0 / 3.0, img.GetSpacing()[2], 6);
                    var d = img.GetDirection();
                    Assert.Equal(0.0, d[2], 6);
                    Assert.Equal(0.0, d[5], 6);
                    Assert.Equal(1.0, d[8], 6);
                    Assert.Equal(0.0, img.GetOrigin()[2], 6);
                }

                // The per-slice positions the rasterizer maps contours with are the true ones.
                Assert.Equal(new[] { 0.0, 1.0, 2.0, 5.0 }, DicomImageSeriesLoader.ReadPerSliceZ(files));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void PublicSpacingPath_MixedGaps_ReturnsTheEndpointAverageWithoutThrowing()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                DicomTestData.WriteCtSeriesWithPixels(dir, DicomTestData.NewUid(), DicomTestData.NewUid(),
                    size: 8, sliceZsMm: new[] { 0.0, 1.0, 2.0, 5.0 });
                var series = Cli.HeadlessRunner.BuildImageSeriesFromFolder(dir);

                double[] spacing = new NiftiConversionService(new RtStructMaskService()).GetImageSpacing(series);

                Assert.Equal(1.0, spacing[0], 6);
                Assert.Equal(1.0, spacing[1], 6);
                Assert.Equal(5.0 / 3.0, spacing[2], 6);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
