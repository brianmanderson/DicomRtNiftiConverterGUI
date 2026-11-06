using System;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Analysis;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The item-6 measures of one ROI, and the reader's handling of malformed structure sets.</summary>
    public class RoiContourGeometryTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>16×16 at 1 mm, slices at z 0, 2, 4, 6.</summary>
        private static ImageGrid Grid() =>
            new ImageGrid(new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0, 0, 0, 1, 0 }, new[] { 1.0, 1.0 }, 16, 16, new[] { 6.0, 0, 2, 4 });

        private static RoiContours Roi(params TestContour[] contours)
        {
            var roi = new RoiContours { RoiNumber = 1, Name = "R", HasContourSequence = true };
            foreach (var c in contours)
            {
                double[] points = null;
                bool failed = false;
                try { points = c.Data.Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); }
                catch (FormatException) { failed = true; }
                roi.Contours.Add(new ContourRecord { GeometricType = c.Type, Points = points, ParseFailed = failed });
            }
            return roi;
        }

        [Fact]
        public void Measures_CountsExtentPlanesAndSlices()
        {
            var facts = RoiContourGeometry.Measure(Roi(
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 0)),
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 2)),
                TestContour.Closed(DicomTestData.SquareContour(3, 6, 2)),
                TestContour.Of("POINT", 5, 5, 4)), Grid());

            Assert.Equal(4, facts.Contours);
            Assert.Equal(4, facts.UsableContours);
            Assert.Equal(13, facts.Points);
            Assert.Equal(2, facts.Planes);
            Assert.Equal(2, facts.SlicesWithContours);
            Assert.Equal(0.0, facts.ZMin);
            Assert.Equal(4.0, facts.ZMax);
            Assert.Equal(4.0, facts.ZExtentMm);
            Assert.Equal(new[] { 2.0, 2, 0 }, facts.BoundsMinMm);
            Assert.Equal(new[] { 8.0, 8, 4 }, facts.BoundsMaxMm);
            Assert.Equal(3, facts.GeometricTypes["CLOSED_PLANAR"]);
            Assert.Equal(1, facts.GeometricTypes["POINT"]);
            Assert.Equal(0, facts.OffPlaneContours);
            Assert.Equal(0, facts.OutsideFovContours);
            Assert.False(facts.Empty);
            Assert.False(facts.Degenerate);
        }

        [Fact]
        public void APlaneBetweenSlices_OrNotOnePlane_IsOffPlane()
        {
            var tilted = DicomTestData.SquareContour(2, 8, 2);
            tilted[8] = 2.5;
            var facts = RoiContourGeometry.Measure(Roi(
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 3)),
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 4.04)),
                TestContour.Closed(tilted)), Grid());

            Assert.Equal(2, facts.OffPlaneContours);
        }

        [Fact]
        public void FieldOfView_SeparatesWhollyOutsideFromClipped()
        {
            var facts = RoiContourGeometry.Measure(Roi(
                TestContour.Closed(DicomTestData.SquareContour(-5, 20, 0)),
                TestContour.Closed(DicomTestData.SquareContour(17, 30, 2)),
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 7.2)),
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 6.9))), Grid());

            // -5..20 encloses the grid: clipped, not outside. 17..30 starts past column 15.5.
            // z 7.2 is past the top slice's reach (6 + 2/2); 6.9 is within it, and off-plane.
            Assert.Equal(2, facts.OutsideFovContours);
            Assert.Equal(1, facts.PartlyOutsideFovContours);
            Assert.Equal(1, facts.OffPlaneContours);
        }

        [Fact]
        public void NoUsableContour_IsEmpty_AndAllDegenerate_IsDegenerate()
        {
            Assert.True(RoiContourGeometry.Measure(new RoiContours { Name = "none" }, Grid()).Empty);
            Assert.True(RoiContourGeometry.Measure(Roi(TestContour.Unparseable(), TestContour.Short(2)), Grid()).Empty);

            var degenerate = RoiContourGeometry.Measure(Roi(
                TestContour.Closed(1, 1, 0, 5, 5, 0),
                TestContour.Closed(1, 1, 0, 5, 5, 0, 9, 9, 0),
                TestContour.Of("OPEN_PLANAR", 4, 4, 0)), Grid());
            Assert.Equal(3, degenerate.DegenerateContours);
            Assert.True(degenerate.Degenerate);
            Assert.False(degenerate.Empty);

            var mixed = RoiContourGeometry.Measure(Roi(
                TestContour.Closed(1, 1, 0, 5, 5, 0),
                TestContour.Closed(DicomTestData.SquareContour(2, 8, 0))), Grid());
            Assert.Equal(1, mixed.DegenerateContours);
            Assert.False(mixed.Degenerate);
        }

        [Fact]
        public void WithoutAGrid_TheGridCountsAreAbsent()
        {
            var facts = RoiContourGeometry.Measure(Roi(TestContour.Closed(DicomTestData.SquareContour(2, 8, 0))), null);

            Assert.False(facts.GridChecked);
            Assert.Null(facts.OffPlaneContours);
            Assert.Null(facts.OutsideFovContours);
            Assert.Null(facts.SlicesWithContours);
            Assert.Equal(1, facts.Planes);
        }

        [Fact]
        public void AnInPlaneRotatedGrid_IsCheckedInItsOwnRowsAndColumns()
        {
            // Rows run along -y, columns along x: the grid covers x 0..15, y -15..0.
            var grid = new ImageGrid(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0, 0, -1, 0 }, new[] { 1.0, 1.0 }, 16, 16, new[] { 0.0 });

            Assert.Equal(0, RoiContourGeometry.Measure(Roi(TestContour.Closed(DicomTestData.SquareContour(-8, -2, 0).Select((v, i) => i % 3 == 0 ? -v : v).ToArray())), grid).OutsideFovContours);
            Assert.Equal(1, RoiContourGeometry.Measure(Roi(TestContour.Closed(DicomTestData.SquareContour(2, 8, 0))), grid).OutsideFovContours);
        }

        [Fact]
        public void Reader_ReportsADamagedFile_AndRepeatedOrOrphanContourItems()
        {
            string damaged = DicomTestData.WriteRtStruct(_dir, "bad.dcm", "P", "1.2", "1.2.3", "1.2.9", "SET", new[] { "Target" });
            DicomTestData.AppendMalformedRoiContourSequence(damaged);
            var read = RtStructContourReader.Read(damaged);
            Assert.NotNull(read.Error);
            Assert.Empty(read.Rois);

            string path = ContourTestData.WriteRtStruct(_dir, "rs.dcm", "P", "1.2", "1.2.4", "1.2.9", new[]
            {
                new TestRoi("A", TestContour.Closed(DicomTestData.SquareContour(2, 8, 0))),
                new TestRoi("B") { Contours = null },
            });
            var file = FellowOakDicom.DicomFile.Open(path);
            var items = file.Dataset.GetSequence(FellowOakDicom.DicomTag.ROIContourSequence).Items;
            var repeat = new FellowOakDicom.DicomDataset { { FellowOakDicom.DicomTag.ReferencedROINumber, "1" } };
            var orphan = new FellowOakDicom.DicomDataset { { FellowOakDicom.DicomTag.ReferencedROINumber, "9" } };
            items.Add(repeat);
            items.Add(orphan);

            var counted = RtStructContourReader.Read(file.Dataset);
            Assert.Equal(1, counted.DuplicateContourItems);
            Assert.Equal(1, counted.OrphanContourItems);
            Assert.True(counted.Rois[0].HasContourSequence);
            Assert.Single(counted.Rois[0].Contours);
            Assert.False(counted.Rois[1].HasContourSequence);
        }
    }
}
