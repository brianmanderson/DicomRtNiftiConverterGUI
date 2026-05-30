using Dicom_RT_images_Csharp.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Tests for C2: the rasterizer must explain WHY an ROI was dropped (KW saw a bare
    /// "no valid contours" with no way to tell that the contours fell off the image grid).
    /// </summary>
    public class RtStructDiagnosticsTests
    {
        [Fact]
        public void NoContoursMessage_BreaksDownCounts_AndHintsWrongSeries_WhenAllOffGrid()
        {
            string msg = RtStructMaskService.BuildNoContoursMessage(
                "GTV", total: 12, parseFailed: 0, tooFewPoints: 0, unhandled: 12,
                geoTypes: new[] { "CLOSED_PLANAR" });

            Assert.Contains("GTV", msg);
            Assert.Contains("0 of 12", msg);
            Assert.Contains("CLOSED_PLANAR", msg);
            Assert.Contains("outside-grid/transform-failed=12", msg);
            Assert.Contains("wrong image series", msg);
        }

        [Fact]
        public void NoContoursMessage_OmitsWrongSeriesHint_WhenParseFailuresDominate()
        {
            string msg = RtStructMaskService.BuildNoContoursMessage(
                "GTV", total: 3, parseFailed: 3, tooFewPoints: 0, unhandled: 0,
                geoTypes: new[] { "CLOSED_PLANAR" });

            Assert.Contains("parse-failed=3", msg);
            Assert.DoesNotContain("wrong image series", msg);
        }

        [Fact]
        public void NoContoursMessage_HandlesEmptyGeoTypes()
        {
            string msg = RtStructMaskService.BuildNoContoursMessage(
                "Empty", total: 0, parseFailed: 0, tooFewPoints: 0, unhandled: 0,
                geoTypes: null);

            Assert.Contains("Empty", msg);
            Assert.Contains("types: none", msg);
        }

        [Fact]
        public void PartialSkipMessage_ReportsSkippedAndRasterizedCounts()
        {
            string msg = RtStructMaskService.BuildPartialSkipMessage(
                "Heart", rasterized: 15, total: 20, unhandled: 5,
                geoTypes: new[] { "CLOSED_PLANAR" });

            Assert.Contains("Heart", msg);
            Assert.Contains("5 of 20", msg);
            Assert.Contains("15 rasterized", msg);
        }
    }
}
