using System.Linq;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// <see cref="ImageGeometry"/>: one series' facts agree with <see cref="SeriesGeometryProbe"/>
    /// (the factor is the one the warning is gated on), and the cohort's distributions count
    /// series, not slices.
    /// </summary>
    public class ImageGeometryTests
    {
        [Fact]
        public void Describe_GivesSpacingXYZ_TheFieldOfView_AndTheWarningsFactor()
        {
            var series = CohortAnalysisFlagTests.Ct("7.1", z: new[] { 0.0, 3.0, 6.0, 12.0, 15.0 });

            var facts = ImageGeometry.Describe(series);

            Assert.Equal(new[] { 0.7, 0.9 }, facts.PixelSpacing);
            Assert.Equal(new[] { 0.9, 0.7, 3.0 }, facts.Spacing);
            Assert.Equal(new[] { 18.0, 11.2 }, facts.FieldOfViewMm);
            Assert.Equal(new[] { 3.0, 6.0 }, facts.SliceSpacingValues);
            Assert.False(facts.SliceSpacingUniform);
            Assert.Equal(15.0, facts.ExtentMm);
            Assert.Equal(0.0, facts.ZMin);
            Assert.Equal(15.0, facts.ZMax);
            // Written spacing 15 / 4 = 3.75: the 6 mm gap is 1.6x it, the 3 mm gaps 1.25x under.
            Assert.Equal(1.6, facts.SpacingErrorFactor);
            Assert.True(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(series, out string warning));
            Assert.Contains("off by up to 1.6x", warning);
        }

        [Fact]
        public void Describe_ASingleSlice_HasNoFactorOrExtentRange()
        {
            var series = CohortAnalysisFlagTests.Ct("7.2", z: new[] { 5.0 });
            series.SliceThickness = 2.5;

            var facts = ImageGeometry.Describe(series);

            Assert.Equal(2.5, facts.SliceSpacing);
            Assert.True(facts.SliceSpacingUniform);
            Assert.Null(facts.SpacingErrorFactor);
            Assert.Equal(1, facts.SliceCount);
        }

        [Fact]
        public void Summarize_CountsSeriesPerValue_AndPerPlane()
        {
            var a = CohortAnalysisFlagTests.Ct("7.3");
            var b = CohortAnalysisFlagTests.Ct("7.4");
            var c = CohortAnalysisFlagTests.Ct("7.5", z: new[] { 0.0, 3.0, 6.0, 9.0 }, orientation: AnalysisCohort.Tilted10);
            c.PixelSpacing = new[] { 0.976562, 0.9765625 };
            c.Rows = 512;
            c.Columns = 512;

            var analysis = CohortAnalysisFlagTests.Analyze(CohortAnalysisFlagTests.Study(a), CohortAnalysisFlagTests.Study(b), CohortAnalysisFlagTests.Study(c));
            var geometry = analysis.Geometry;

            Assert.Equal(3, geometry.ImageSeries);
            Assert.Equal(2, geometry.Planes[SeriesOrientation.Axial]);
            Assert.Equal(1, geometry.Planes[SeriesOrientation.Oblique]);
            Assert.Equal(0, geometry.Planes["unknown"]);

            Assert.Equal(3, geometry.SliceSpacingMm.Series);
            Assert.Equal(2.0, geometry.SliceSpacingMm.Min);
            Assert.Equal(2.0, geometry.SliceSpacingMm.Median);
            Assert.Equal(3.0, geometry.SliceSpacingMm.Max);
            Assert.Equal(new[] { (2.0, 2), (3.0, 1) }, geometry.SliceSpacingMm.Values.Select(v => (v.Value, v.Series)));

            // Values count to 4 decimals, so 0.976562 and 0.9765625 are one bar of the histogram.
            Assert.Equal(new[] { (0.9, 2), (0.9766, 1) }, geometry.PixelSpacingXMm.Values.Select(v => (v.Value, v.Series)));

            Assert.Equal(new[] { (16, 20, 2), (512, 512, 1) },
                geometry.Grids.Select(g => (g.Rows.Value, g.Columns.Value, g.Series)));
        }
    }
}
