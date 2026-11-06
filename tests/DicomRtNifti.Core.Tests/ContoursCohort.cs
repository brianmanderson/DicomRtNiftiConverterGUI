using System.IO;
using System.Linq;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A small cohort written with fixed identifiers that raises every flag code of the contours
    /// tier once or more, so its document is the checked-in example of
    /// dicomrtnifti.cohort-analysis/1 at --tier contours. Headers and contours only; the doses
    /// carry pixels, because the audit reads their maximum.
    ///
    ///   CN_01  study 3.1.1: a 16×16×5 CT at 1 mm, slices at z 0, 2, 4, 6, 8. Its structure set:
    ///          Body (clipped by the field of view, not flagged), GTV (clean), Lost (500 mm off in
    ///          x: ROI_OUTSIDE_FOV), Shifted (on z 3, between slices: ROI_OFF_PLANE), Line (a two-point
    ///          closed contour: ROI_DEGENERATE), Nothing (no contours) and Broken (unparseable
    ///          ContourData), both ROI_EMPTY. An axial dose at 2 mm that covers the CT but not Body
    ///          or Lost (DOSE_NOT_COVERING_ROI), and a dose rotated 90 degrees (DOSE_NOT_ALIGNED).
    ///   CN_02  study 3.2.1: a CT whose structure set's contour data is damaged (RT_UNREADABLE).
    /// </summary>
    internal static class ContoursCohort
    {
        public const string Salt = "CONTOURS";

        private static double[] Square(double min, double max, double z) => DicomTestData.SquareContour(min, max, z);

        public static void Write(string input)
        {
            // CN_01
            string a = Path.Combine(input, "CN_01");
            foreach (int z in new[] { 0, 2, 4, 6, 8 })
                ContourTestData.WriteSlice(Path.Combine(a, "ct"), $"ct_{z}.dcm", "CN_01", "3.1.1", "3.1.1.1", "3.1.1.90", z);
            ContourTestData.WriteRtStruct(Path.Combine(a, "rt"), "rs.dcm", "CN_01", "3.1.1", "3.1.1.3", "3.1.1.90", new[]
            {
                new TestRoi("Body", new[] { 0, 2, 4, 6, 8 }.Select(z => TestContour.Closed(Square(-5, 20, z))).ToArray()),
                new TestRoi("GTV", TestContour.Closed(Square(4, 9, 2)), TestContour.Closed(Square(4, 9, 4))),
                new TestRoi("Lost", TestContour.Closed(Square(503, 510, 4))),
                new TestRoi("Shifted", TestContour.Closed(Square(4, 9, 3))),
                new TestRoi("Line", TestContour.Closed(2, 2, 4, 9, 9, 4)),
                new TestRoi("Nothing") { Contours = null },
                new TestRoi("Broken", TestContour.Unparseable()),
            }, referencedSeriesUid: "3.1.1.1");
            var frames = new[] { 0.0, 2, 4, 6, 8 };
            ContourTestData.WriteDose(Path.Combine(a, "rt"), "dose_axial.dcm", ContourTestData.Dose(
                "CN_01", "3.1.1", "3.1.1.5", "3.1.1.90", new[] { 1.0, 1, 0 }, new[] { 1.0, 0, 0, 0, 1, 0 },
                new[] { 2.0, 2.0 }, 8, 8, frames));
            ContourTestData.WriteDose(Path.Combine(a, "rt"), "dose_rotated.dcm", ContourTestData.Dose(
                "CN_01", "3.1.1", "3.1.1.6", "3.1.1.90", new[] { 15.0, 0, 0 }, new[] { 0.0, 1, 0, -1, 0, 0 },
                new[] { 1.0, 1.0 }, 16, 16, frames));

            // CN_02
            string b = Path.Combine(input, "CN_02");
            foreach (int z in new[] { 0, 1, 2 })
                ContourTestData.WriteSlice(Path.Combine(b, "ct"), $"ct_{z}.dcm", "CN_02", "3.2.1", "3.2.1.1", "3.2.1.90", z);
            Directory.CreateDirectory(Path.Combine(b, "rt"));
            string damaged = DicomTestData.WriteRtStruct(Path.Combine(b, "rt"), "rs.dcm", "CN_02", "3.2.1", "3.2.1.3", "3.2.1.90",
                "DAMAGED", new[] { "Target" }, referencedSeriesUid: "3.2.1.1");
            DicomTestData.AppendMalformedRoiContourSequence(damaged);
        }
    }
}
