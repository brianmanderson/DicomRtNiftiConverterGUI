using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Toolkit issue 70: <see cref="SeriesGeometry.Spacing"/> is [x, y, z], and DICOM's
    /// PixelSpacing is [row, column] — the spacing between rows (y), then between columns (x).
    /// Square pixels hid the swap; a non-square grid shows it.
    /// </summary>
    public class SeriesGeometrySpacingTests
    {
        [Fact]
        public void Spacing_IsColumnThenRowThenSlice_ForNonSquarePixels()
        {
            var series = new DicomSeriesGroup
            {
                SeriesInstanceUID = "1.2.3",
                Modality = "CT",
                FilePaths = { "a", "b", "c" },
                SlicePositions = { 0.0, 2.5, 5.0 },
                PixelSpacing = new[] { 0.7, 0.9 },
            };

            Assert.True(SeriesGeometryProbe.TryDescribe(series, out var geometry));

            Assert.Equal(new[] { 0.7, 0.9 }, geometry.PixelSpacing);
            Assert.Equal(new[] { 0.9, 0.7, 2.5 }, geometry.Spacing);
        }

        [Fact]
        public void Spacing_IsNull_WithoutPixelSpacing()
        {
            var series = new DicomSeriesGroup { FilePaths = { "a", "b" }, SlicePositions = { 0.0, 1.0 } };

            Assert.True(SeriesGeometryProbe.TryDescribe(series, out var geometry));
            Assert.Null(geometry.Spacing);
        }
    }
}
