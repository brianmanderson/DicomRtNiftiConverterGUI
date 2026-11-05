using DicomRtNifti.Core.Analysis;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// <see cref="SeriesOrientation"/>: which way the slices are stacked. The tilt is what
    /// <see cref="FlagCodes.GeomOblique"/> reports (toolkit issue 44's case); an in-plane rotation
    /// keeps the normal on z and is not tilted.
    /// </summary>
    public class SeriesOrientationTests
    {
        [Theory]
        [InlineData(new[] { 1.0, 0, 0, 0, 1, 0 }, "axial", true, 0.0)]
        [InlineData(new[] { 1.0, 0, 0, 0, 0, -1 }, "coronal", true, 90.0)]
        [InlineData(new[] { 0.0, 1, 0, 0, 0, -1 }, "sagittal", true, 90.0)]
        [InlineData(new[] { -1.0, 0, 0, 0, -1, 0 }, "axial", true, 0.0)]
        [InlineData(new[] { 0.866025, 0.5, 0, -0.5, 0.866025, 0 }, "axial", false, 0.0)]
        [InlineData(new[] { 1.0, 0, 0, 0, 0.984808, 0.173648 }, "oblique", false, 10.0)]
        public void Describes_ThePlane_TheAlignment_AndTheTilt(double[] iop, string plane, bool axisAligned, double tilt)
        {
            var orientation = SeriesOrientation.FromImageOrientation(iop);

            Assert.Equal(plane, orientation.Plane);
            Assert.Equal(axisAligned, orientation.AxisAligned);
            Assert.Equal(tilt, orientation.NormalTiltDegrees, 3);
            Assert.Equal(tilt > SeriesOrientation.TiltToleranceDegrees, orientation.IsTilted);
        }

        [Fact]
        public void ATiltBelowTheTolerance_IsAxialAndNotFlagged()
        {
            // 0.05 degrees about x: cos = 0.99999962, sin = 0.00087266.
            var orientation = SeriesOrientation.FromImageOrientation(new[] { 1.0, 0, 0, 0, 0.99999962, 0.00087266 });

            Assert.Equal(SeriesOrientation.Axial, orientation.Plane);
            Assert.False(orientation.IsTilted);
        }

        [Fact]
        public void TheNormal_IsRowCrossColumn()
        {
            var orientation = SeriesOrientation.FromImageOrientation(new[] { 1.0, 0, 0, 0, 0, -1 });

            Assert.Equal(new[] { 0.0, 1.0, 0.0 }, orientation.SliceNormal);
        }

        [Theory]
        [InlineData(new[] { 1.0, 0, 0, 0, 1 })]
        [InlineData(new[] { 0.0, 0, 0, 0, 1, 0 })]
        [InlineData(new[] { 1.0, 0, 0, 1, 0, 0 })]
        public void NoPlane_IsNull(double[] iop)
        {
            Assert.Null(SeriesOrientation.FromImageOrientation(iop));
            Assert.Null(SeriesOrientation.FromImageOrientation(null));
        }
    }
}
