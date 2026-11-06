using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The contours tier parses ROIContourSequence apart from the rasterizer (web plan risk R-3);
    /// these tests bound the drift. On structure sets with unparseable, too-short and off-grid
    /// contours, the reader's per-ROI counts equal the ones the rasterizer reports in its
    /// "Skipping ROI" and partial-skip diagnostics (the inputs of BuildNoContoursMessage and
    /// BuildPartialSkipMessage): contours, parse-failed, too-few-points, geometric types, and the
    /// rasterizer's outside-grid count against the reader's wholly-outside-the-field-of-view
    /// count. The off-grid contours here miss the grid by far more than the rasterizer's one-grid
    /// allowance; nearer misses are where the two may part (see the last test), which is why the
    /// analysis reports geometry and never the rasterizer's verdict.
    /// </summary>
    public class RtStructContourAgreementTests : IDisposable
    {
        private const int Size = 16, Slices = 4;
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static double[] Square(double offsetX, double offsetY, double z) =>
            DicomTestData.SquareContour(3, 10, z).Select((v, i) => i % 3 == 0 ? v + offsetX : i % 3 == 1 ? v + offsetY : v).ToArray();

        /// <summary>The 16×16×4 reference at 1 mm, origin 0: patient mm equal voxel indices.</summary>
        private static Image Reference()
        {
            var img = new Image(Size, Size, Slices, PixelIDValueEnum.sitkInt16);
            img.SetOrigin(new VectorDouble(new[] { 0.0, 0.0, 0.0 }));
            img.SetSpacing(new VectorDouble(new[] { 1.0, 1.0, 1.0 }));
            return img;
        }

        private static ImageGrid Grid() =>
            new ImageGrid(new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0, 0, 0, 1, 0 }, new[] { 1.0, 1.0 }, Size, Size,
                Enumerable.Range(0, Slices).Select(i => (double)i));

        private static IEnumerable<TestRoi> Rois() => new[]
        {
            new TestRoi("Good", TestContour.Closed(Square(0, 0, 0)), TestContour.Closed(Square(0, 0, 1)), TestContour.Closed(Square(0, 0, 2))),
            new TestRoi("Unreadable", TestContour.Unparseable(), TestContour.Unparseable(), TestContour.Short(2), TestContour.Short(0)),
            new TestRoi("OffGrid",
                TestContour.Closed(Square(1000, 0, 0)),
                TestContour.Of("OPEN_PLANAR", Square(0, -900, 1)),
                TestContour.Of("POINT", 5000, 5, 1),
                TestContour.Of("CLOSED_NONPLANAR", 400, 400, 0, 410, 400, 1, 410, 410, 2)),
            new TestRoi("Mixed", TestContour.Unparseable(), TestContour.Short(1), TestContour.Closed(Square(-500, 0, 0)), TestContour.Closed(Square(0, 700, 2))),
            new TestRoi("Partial", TestContour.Closed(Square(0, 0, 1)), TestContour.Closed(Square(0, 0, 2)), TestContour.Closed(Square(0, 800, 3))),
        };

        private sealed class Lines : IProgress<string>
        {
            public readonly List<string> All = new List<string>();
            public void Report(string value) { lock (All) All.Add(value); }
        }

        /// <summary>What the rasterizer said about one ROI: its diagnostic's counts, or null when it said nothing.</summary>
        private sealed class Diagnostic
        {
            public int Total, ParseFailed = -1, TooFewPoints = -1, Unhandled, Rasterized;
            public HashSet<string> Types;
        }

        private static readonly Regex NoContours = new Regex(
            @"Skipping ROI '(?<n>[^']*)': 0 of (?<t>\d+) contour\(s\) rasterized \[types: (?<ty>[^\]]*)\] " +
            @"\(outside-grid/transform-failed=(?<u>\d+), parse-failed=(?<p>\d+), too-few-points=(?<f>\d+)\)");

        private static readonly Regex Partial = new Regex(
            @"ROI '(?<n>[^']*)': (?<u>\d+) of (?<t>\d+) contour\(s\) skipped \(outside grid / transform failed\) " +
            @"\[types: (?<ty>[^\]]*)\]; (?<r>\d+) rasterized");

        private static Dictionary<string, Diagnostic> Rasterize(string path, IReadOnlyList<double> sliceZs, out HashSet<string> masks)
        {
            var lines = new Lines();
            var names = Rois().ToDictionary(r => r.Name, r => r.Name);
            var result = new RtStructMaskService().RasterizeRois(path, Reference(), names, lines, CancellationToken.None, sliceZs);
            masks = new HashSet<string>(result.Keys);
            foreach (var mask in result.Values) mask.Dispose();

            var byRoi = new Dictionary<string, Diagnostic>();
            foreach (var line in lines.All)
            {
                var m = NoContours.Match(line);
                if (m.Success)
                {
                    byRoi[m.Groups["n"].Value] = new Diagnostic
                    {
                        Total = int.Parse(m.Groups["t"].Value), Unhandled = int.Parse(m.Groups["u"].Value),
                        ParseFailed = int.Parse(m.Groups["p"].Value), TooFewPoints = int.Parse(m.Groups["f"].Value),
                        Types = Types(m.Groups["ty"].Value),
                    };
                    continue;
                }
                m = Partial.Match(line);
                if (m.Success)
                {
                    byRoi[m.Groups["n"].Value] = new Diagnostic
                    {
                        Total = int.Parse(m.Groups["t"].Value), Unhandled = int.Parse(m.Groups["u"].Value),
                        Rasterized = int.Parse(m.Groups["r"].Value), Types = Types(m.Groups["ty"].Value),
                    };
                }
            }
            return byRoi;
        }

        private static HashSet<string> Types(string list) =>
            new HashSet<string>(list.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Where(t => t != "none"));

        public static IEnumerable<object[]> SliceLookups() => new[]
        {
            new object[] { false },
            new object[] { true },
        };

        [Theory]
        [MemberData(nameof(SliceLookups))]
        public void ReaderCounts_EqualTheRasterizersDiagnosticCounts(bool perSliceZ)
        {
            string path = ContourTestData.WriteRtStruct(_dir, "rs.dcm", "PAT", "1.2", "1.2.3", "1.2.9", Rois());
            var sliceZs = perSliceZ ? Enumerable.Range(0, Slices).Select(i => (double)i).ToArray() : null;

            var diagnostics = Rasterize(path, sliceZs, out var masks);
            var read = RtStructContourReader.Read(path);

            Assert.Null(read.Error);
            Assert.Equal(new[] { "Mixed", "OffGrid", "Unreadable" }, diagnostics.Where(d => d.Value.ParseFailed >= 0).Select(d => d.Key).OrderBy(n => n));
            Assert.Equal(new[] { "Partial" }, diagnostics.Where(d => d.Value.ParseFailed < 0).Select(d => d.Key));

            foreach (var roi in read.Rois)
            {
                var facts = RoiContourGeometry.Measure(roi, Grid());
                if (!diagnostics.TryGetValue(roi.Name, out var said))
                {
                    // Nothing reported: every contour rasterized, and the reader sees no failure either.
                    Assert.Contains(roi.Name, masks);
                    Assert.Equal(0, facts.ParseFailed);
                    Assert.Equal(0, facts.TooFewPoints);
                    Assert.Equal(0, facts.OutsideFovContours);
                    continue;
                }

                Assert.True(said.Total == facts.Contours, $"{roi.Name}: contours {said.Total} vs {facts.Contours}");
                Assert.True(said.Unhandled == facts.OutsideFovContours, $"{roi.Name}: outside-grid {said.Unhandled} vs outside-FOV {facts.OutsideFovContours}");
                Assert.Equal(said.Types, new HashSet<string>(facts.GeometricTypes.Keys));
                if (said.ParseFailed >= 0)
                {
                    Assert.True(said.ParseFailed == facts.ParseFailed, $"{roi.Name}: parse-failed {said.ParseFailed} vs {facts.ParseFailed}");
                    Assert.True(said.TooFewPoints == facts.TooFewPoints, $"{roi.Name}: too-few-points {said.TooFewPoints} vs {facts.TooFewPoints}");
                    Assert.DoesNotContain(roi.Name, masks);
                }
                else
                {
                    Assert.Equal(said.Total - said.Unhandled, said.Rasterized);
                    Assert.Equal(said.Rasterized, facts.UsableContours - facts.OutsideFovContours);
                    Assert.Contains(roi.Name, masks);
                }
            }
        }

        /// <summary>The reader's ROIs and the counts it feeds, independently of the rasterizer.</summary>
        [Fact]
        public void Reader_ReadsNumbersNamesTypesAndPoints()
        {
            string path = ContourTestData.WriteRtStruct(_dir, "rs.dcm", "PAT", "1.2", "1.2.3", "1.2.9", Rois());
            var read = RtStructContourReader.Read(path);

            Assert.Equal(new[] { "Good", "Unreadable", "OffGrid", "Mixed", "Partial" }, read.Rois.Select(r => r.Name));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, read.Rois.Select(r => r.RoiNumber));
            var offGrid = read.Rois[2];
            Assert.Equal(new[] { "CLOSED_PLANAR", "OPEN_PLANAR", "POINT", "CLOSED_NONPLANAR" }, offGrid.Contours.Select(c => c.GeometricType));
            Assert.Equal(new[] { 4, 4, 1, 3 }, offGrid.Contours.Select(c => c.PointCount));

            var unreadable = RoiContourGeometry.Measure(read.Rois[1], Grid());
            Assert.Equal(2, unreadable.ParseFailed);
            Assert.Equal(2, unreadable.TooFewPoints);
            Assert.True(unreadable.Empty);
        }

        /// <summary>
        /// Where the two part, recorded so the difference is seen rather than assumed away: a
        /// planar contour above the last slice is outside the field of view, and the rasterizer,
        /// given the per-slice z the export always passes, fills it on the nearest slice instead.
        /// </summary>
        [Fact]
        public void APlanarContourAboveTheScan_IsOutsideTheFieldOfView_AndTheRasterizerFillsTheTopSlice()
        {
            var rois = new[] { new TestRoi("Above", TestContour.Closed(Square(0, 0, 50))) };
            string path = ContourTestData.WriteRtStruct(_dir, "above.dcm", "PAT", "1.2", "1.2.3", "1.2.9", rois);
            var sliceZs = Enumerable.Range(0, Slices).Select(i => (double)i).ToArray();

            var masks = new RtStructMaskService().RasterizeRois(path, Reference(),
                new Dictionary<string, string> { { "Above", "Above" } }, null, CancellationToken.None, sliceZs);
            var facts = RoiContourGeometry.Measure(RtStructContourReader.Read(path).Rois[0], Grid());

            Assert.Equal(1, facts.OutsideFovContours);
            Assert.True(masks.ContainsKey("Above"));
            foreach (var mask in masks.Values) mask.Dispose();
        }
    }
}
