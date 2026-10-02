using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Cli;
using DicomRtNifti.Core.Models;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The explicit --forward route is handed an RTSTRUCT and an image folder with nothing
    /// linking them. It now compares what the structure set says it references (the series UID,
    /// else the frame of reference) with the series it is about to rasterize onto: a warning by
    /// default, exit 1 under --strict-reference (D-10).
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CliReferenceCheckTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _images;
        private readonly string _studyUid = DicomTestData.NewUid();
        private readonly string _ctSeriesUid = DicomTestData.NewUid();
        private readonly string _frameUid = DicomTestData.NewUid();

        public CliReferenceCheckTests()
        {
            _dir = DicomTestData.NewTempDir();
            _images = Path.Combine(_dir, "ct");
            DicomTestData.WriteCtSeriesWithPixels(_images, _ctSeriesUid, _frameUid,
                sliceCount: 4, size: 16, studyUid: _studyUid);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private static IList<KeyValuePair<string, double[]>> OneSquare() =>
            new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
            };

        private CliRun Forward(string rtstruct, params string[] extra)
        {
            var args = new List<string>
            {
                "--forward", "--rtstruct", rtstruct, "--image-folder", _images,
                "--output-folder", Path.Combine(_dir, "out_" + Guid.NewGuid().ToString("N").Substring(0, 6)),
            };
            args.AddRange(extra);
            return CliRun.Execute(args.ToArray());
        }

        [Fact]
        public void MatchingReferences_NoWarning_Exit0()
        {
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_ok.dcm",
                _studyUid, DicomTestData.NewUid(), _frameUid, OneSquare(), referencedSeriesUid: _ctSeriesUid);

            var run = Forward(rs);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.DoesNotContain("WARNING: the RTSTRUCT", run.Stderr);
            Assert.Contains("Target\t", run.Stdout);
        }

        [Fact]
        public void ReferencedSeriesDiffers_WarnsAndStillRasterizes()
        {
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_other_series.dcm",
                _studyUid, DicomTestData.NewUid(), _frameUid, OneSquare(), referencedSeriesUid: DicomTestData.NewUid());

            var run = Forward(rs);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("WARNING: the RTSTRUCT references image series", run.Stderr);
            Assert.Contains("--strict-reference", run.Stderr);
            Assert.Contains("Target\t", run.Stdout);
        }

        [Fact]
        public void ReferencedSeriesDiffers_StrictReference_Exits1BeforeRasterizing()
        {
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_other_series.dcm",
                _studyUid, DicomTestData.NewUid(), _frameUid, OneSquare(), referencedSeriesUid: DicomTestData.NewUid());

            var run = Forward(rs, "--strict-reference");
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("ERROR: the RTSTRUCT references image series", run.Stderr);
            Assert.Contains("Refusing to rasterize", run.Stderr);
            Assert.DoesNotContain("Rasterizing ROI", run.Stderr);
            Assert.DoesNotContain("rt_mask_validation forward", run.Stdout);
        }

        [Fact]
        public void StructureSetReferencingSeveralSeries_MatchesWhenTheImageIsAnyOfThem()
        {
            // A planning CT and a registered MR in one structure set: the image folder holds the
            // second referenced series, which is a match, not a mismatch, even under strict.
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_two_series.dcm",
                _studyUid, DicomTestData.NewUid(), _frameUid, OneSquare(), referencedSeriesUid: DicomTestData.NewUid());
            var file = FellowOakDicom.DicomFile.Open(rs);
            var seriesSeq = file.Dataset
                .GetSequence(FellowOakDicom.DicomTag.ReferencedFrameOfReferenceSequence).Items[0]
                .GetSequence(FellowOakDicom.DicomTag.RTReferencedStudySequence).Items[0]
                .GetSequence(FellowOakDicom.DicomTag.RTReferencedSeriesSequence);
            seriesSeq.Items.Add(new FellowOakDicom.DicomDataset { { FellowOakDicom.DicomTag.SeriesInstanceUID, _ctSeriesUid } });
            file.Save(rs);

            var run = Forward(rs, "--strict-reference");
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.DoesNotContain("references image series", run.Stderr);
        }

        [Fact]
        public void FrameOfReferenceDiffers_WithoutSeriesReference_Warns()
        {
            // No RTReferencedSeriesSequence: only the frame of reference is checkable, and it is
            // another scan's.
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_other_frame.dcm",
                _studyUid, DicomTestData.NewUid(), DicomTestData.NewUid(), OneSquare());

            var run = Forward(rs);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("WARNING: the RTSTRUCT's frame of reference", run.Stderr);
        }

        [Fact]
        public void DescribeReferenceMismatch_PrefersTheSeriesUidOverTheFrame()
        {
            var image = new DicomSeriesGroup { SeriesInstanceUID = "S1", FrameOfReferenceUID = "F1" };

            Assert.Null(HeadlessRunner.DescribeReferenceMismatch(
                new DicomSeriesGroup { ReferencedSeriesUID = "S1", FrameOfReferenceUID = "F9" }, image));
            Assert.Contains("references image series S2", HeadlessRunner.DescribeReferenceMismatch(
                new DicomSeriesGroup { ReferencedSeriesUID = "S2", FrameOfReferenceUID = "F1" }, image));
            Assert.Contains("frame of reference F2", HeadlessRunner.DescribeReferenceMismatch(
                new DicomSeriesGroup { ReferencedSeriesUID = "", FrameOfReferenceUID = "F2" }, image));
            Assert.Null(HeadlessRunner.DescribeReferenceMismatch(
                new DicomSeriesGroup { ReferencedSeriesUID = "", FrameOfReferenceUID = "" }, image));

            // Membership over every referenced series and frame.
            Assert.Null(HeadlessRunner.DescribeReferenceMismatch(new[] { "S9", "S1" }, new string[0], image));
            Assert.Null(HeadlessRunner.DescribeReferenceMismatch(new string[0], new[] { "F9", "F1" }, image));
            Assert.Contains("S8, S9", HeadlessRunner.DescribeReferenceMismatch(new[] { "S8", "S9" }, new[] { "F1" }, image));
        }
    }
}
