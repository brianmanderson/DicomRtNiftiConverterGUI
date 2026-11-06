using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Analysis;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The item-7 dose audit: header facts, alignment with the image, coverage of each ROI.</summary>
    public class DoseGridAuditTests
    {
        private static readonly double[] Axial = { 1, 0, 0, 0, 1, 0 };

        /// <summary>16Ã—16 at 1 mm, slices at z 0..3.</summary>
        private static ImageGrid Image() =>
            new ImageGrid(new[] { 0.0, 0, 0 }, Axial, new[] { 1.0, 1.0 }, 16, 16, new[] { 0.0, 1, 2, 3 });

        private static KeyValuePair<string, RoiContours> Roi(string name, double min, double max, double z)
        {
            var roi = new RoiContours { Name = name };
            roi.Contours.Add(new ContourRecord { GeometricType = "CLOSED_PLANAR", Points = DicomTestData.SquareContour(min, max, z) });
            return new KeyValuePair<string, RoiContours>(name, roi);
        }

        [Fact]
        public void ACoarserAxialDose_IsAligned_NotTheSameGrid_AndReportsItsHeader()
        {
            // 2 mm voxels at (1, 1, 0.5): centres 1..11 in x and y, frames 0.5 and 2.5.
            var dose = ContourTestData.Dose("P", "1", "2", "3", new[] { 1.0, 1, 0.5 }, Axial, new[] { 2.0, 2.0 }, 6, 6, new[] { 0.0, 2.0 });

            var facts = DoseGridAudit.Audit(dose, Image(), new[] { 1.0, 1, 1 },
                new[] { Roi("Inside", 3, 9, 1), Roi("Wide", 3, 14, 2) });

            Assert.Null(facts.Error);
            Assert.Equal("GY", facts.DoseUnits);
            Assert.Equal("PHYSICAL", facts.DoseType);
            Assert.Equal("PLAN", facts.DoseSummationType);
            Assert.Equal(6, facts.Rows);
            Assert.Equal(6, facts.Columns);
            Assert.Equal(2, facts.Frames);
            Assert.Equal(new[] { 2.0, 2, 2 }, facts.VoxelSizeMm);
            Assert.Equal(0.02, facts.MaxDose);
            Assert.Equal(new[] { 0.0, 0, -0.5 }, facts.ExtentMinMm);
            Assert.Equal(new[] { 12.0, 12, 3.5 }, facts.ExtentMaxMm);
            Assert.True(facts.OrientationMatchesImage);
            Assert.False(facts.SameGridAsImage);

            Assert.True(facts.RoiCoverage.Single(c => c.Roi == "Inside").Covered);
            var wide = facts.RoiCoverage.Single(c => c.Roi == "Wide");
            Assert.False(wide.Covered);
            Assert.Equal(0.25, wide.PointsInside);
        }

        [Fact]
        public void ADoseOnTheImagesOwnVoxels_IsTheSameGrid()
        {
            var dose = ContourTestData.Dose("P", "1", "2", "3", new[] { 0.0, 0, 0 }, Axial, new[] { 1.0, 1.0 }, 16, 16, new[] { 0.0, 1, 2, 3 });

            var facts = DoseGridAudit.Audit(dose, Image(), null, null);

            Assert.True(facts.SameGridAsImage);
            Assert.Empty(facts.RoiCoverage);
        }

        [Fact]
        public void ARotatedDose_IsNotAligned()
        {
            var dose = ContourTestData.Dose("P", "1", "2", "3", new[] { 15.0, 0, 0 }, new[] { 0.0, 1, 0, -1, 0, 0 }, new[] { 1.0, 1.0 }, 16, 16, new[] { 0.0 });

            var facts = DoseGridAudit.Audit(dose, Image(), null, null);

            Assert.False(facts.OrientationMatchesImage);
            Assert.False(facts.SameGridAsImage);
        }

        /// <summary>GridFrameOffsetVector whose first value is not 0 holds the frames' own z (PS3.3 C.8.8.3.2).</summary>
        [Fact]
        public void AbsoluteFrameOffsets_PlaceTheFramesAtTheirOwnZ()
        {
            var dose = ContourTestData.Dose("P", "1", "2", "3", new[] { 0.0, 0, 10 }, Axial, new[] { 1.0, 1.0 }, 4, 4, new[] { 10.0, 12, 14 });

            var facts = DoseGridAudit.Audit(dose, null, null, new[] { Roi("Top", 1, 2, 14.5), Roi("Above", 1, 2, 16) });

            Assert.Equal(9.0, facts.ExtentMinMm[2]);
            Assert.Equal(15.0, facts.ExtentMaxMm[2]);
            Assert.Null(facts.OrientationMatchesImage);
            Assert.True(facts.RoiCoverage.Single(c => c.Roi == "Top").Covered);
            Assert.False(facts.RoiCoverage.Single(c => c.Roi == "Above").Covered);
        }

        [Fact]
        public void ADoseWithoutAGrid_ReportsItsHeaderOnly()
        {
            var dose = ContourTestData.Dose("P", "1", "2", "3", new[] { 0.0, 0, 0 }, Axial, new[] { 1.0, 1.0 }, 2, 2, new[] { 0.0 }, units: "RELATIVE");
            dose.Remove(FellowOakDicom.DicomTag.ImagePositionPatient);

            var facts = DoseGridAudit.Audit(dose, Image(), null, new[] { Roi("Inside", 3, 9, 1) });

            Assert.Equal("RELATIVE", facts.DoseUnits);
            Assert.Null(facts.ExtentMinMm);
            Assert.Null(facts.OrientationMatchesImage);
            Assert.Empty(facts.RoiCoverage);
        }
    }
}
