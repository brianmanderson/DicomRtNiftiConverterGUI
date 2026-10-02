using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Cli;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A slice whose ImagePositionPatient is absent or unparseable cannot be placed along the
    /// slice axis. The CLI's series builder used to drop the absent case silently and keep the
    /// unparseable one; the scanner kept both; the sorter then put a kept slice at z = 0 and the
    /// spacing probe read the hole in the position list as non-uniform spacing. Now both builders
    /// skip the slice, record it, and the probe names it.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class UnpositionedSliceTests : IDisposable
    {
        private readonly string _dir;

        public UnpositionedSliceTests()
        {
            _dir = DicomTestData.NewTempDir();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string WriteSeries(string name, IDictionary<int, string[]> overrides, out string seriesUid, out string frameUid, out string studyUid)
        {
            string dir = Path.Combine(_dir, name);
            seriesUid = DicomTestData.NewUid();
            frameUid = DicomTestData.NewUid();
            studyUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(dir, seriesUid, frameUid, sliceCount: 4, size: 8,
                studyUid: studyUid, ippOverrides: overrides);
            return dir;
        }

        private static async Task<DicomSeriesGroup> ScanCtSeriesAsync(string dir)
        {
            var scan = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
            return scan.Patients.Single().Studies.Single().Series.Single(s => s.Modality == "CT");
        }

        [Fact]
        public void CliBuilder_SkipsAnUnparseableSlice_AndNamesIt()
        {
            string dir = WriteSeries("unparseable", new Dictionary<int, string[]> { { 1, new[] { "0", "0", "abc" } } },
                out _, out _, out _);

            var series = HeadlessRunner.BuildImageSeriesFromFolder(dir);

            Assert.Equal(3, series.FilePaths.Count);
            Assert.Equal(new[] { 0.0, 2.0, 3.0 }, series.SlicePositions.OrderBy(z => z).ToArray());
            string label = Assert.Single(series.UnpositionedSlices);
            Assert.Contains("ct_001.dcm", label);
            Assert.Contains("InstanceNumber 2", label);
            Assert.Contains("unparseable", label);
        }

        [Fact]
        public async Task Scanner_SkipsTheSameSlice_AndRecordsTheSameLabel()
        {
            string dir = WriteSeries("agree", new Dictionary<int, string[]> { { 1, new[] { "0", "0", "abc" } } },
                out _, out _, out _);

            var fromCli = HeadlessRunner.BuildImageSeriesFromFolder(dir);
            var fromScan = await ScanCtSeriesAsync(dir);

            Assert.Equal(3, fromScan.FilePaths.Count);
            Assert.Equal(fromCli.FilePaths.Select(Path.GetFileName).OrderBy(f => f).ToArray(),
                         fromScan.FilePaths.Select(Path.GetFileName).OrderBy(f => f).ToArray());
            Assert.Equal(fromCli.SlicePositions.OrderBy(z => z).ToArray(), fromScan.SlicePositions.OrderBy(z => z).ToArray());
            Assert.Equal(fromCli.UnpositionedSlices, fromScan.UnpositionedSlices);
        }

        [Fact]
        public async Task AbsentPosition_IsSkippedAndNamedAsMissing_ByBothBuilders()
        {
            string dir = WriteSeries("absent", new Dictionary<int, string[]> { { 2, null } }, out _, out _, out _);

            var fromCli = HeadlessRunner.BuildImageSeriesFromFolder(dir);
            var fromScan = await ScanCtSeriesAsync(dir);

            foreach (var series in new[] { fromCli, fromScan })
            {
                Assert.Equal(3, series.FilePaths.Count);
                string label = Assert.Single(series.UnpositionedSlices);
                Assert.Contains("ct_002.dcm", label);
                Assert.Contains("missing", label);
            }
        }

        [Fact]
        public async Task RtStructWithoutPosition_IsNotReportedAsUnpositioned()
        {
            string dir = WriteSeries("with_rs", null, out string ctUid, out string frameUid, out string studyUid);
            DicomTestData.WriteRtStructWithContours(dir, "rs.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(1.5, 5.5, 1.0)),
                },
                referencedSeriesUid: ctUid);

            var scan = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
            var study = scan.Patients.Single().Studies.Single();
            var ct = study.Series.Single(s => s.Modality == "CT");
            var rs = study.Series.Single(s => s.Modality == "RTSTRUCT");

            Assert.Equal(4, ct.FilePaths.Count);
            Assert.Empty(ct.UnpositionedSlices);
            Assert.Single(rs.FilePaths);
            Assert.Empty(rs.UnpositionedSlices);
            Assert.Empty(HeadlessRunner.BuildImageSeriesFromFolder(dir).UnpositionedSlices);
        }

        [Fact]
        public void Forward_WarnsAboutTheSkippedSlice_BeforeTheSpacingWarning()
        {
            string dir = WriteSeries("forward", new Dictionary<int, string[]> { { 1, new[] { "0", "0", "abc" } } },
                out string ctUid, out string frameUid, out string studyUid);
            string rs = DicomTestData.WriteRtStructWithContours(_dir, "rs_forward.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(1.5, 5.5, 2.0)),
                },
                referencedSeriesUid: ctUid);

            var run = CliRun.Execute("--forward", "--rtstruct", rs, "--image-folder", dir,
                "--output-folder", Path.Combine(_dir, "out"));

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("Image series: 3 files", run.Stderr);
            int missingAt = run.Stderr.IndexOf("1 of 4 image slice(s) carry no usable ImagePositionPatient", StringComparison.Ordinal);
            int spacingAt = run.Stderr.IndexOf("has non-uniform slice spacing", StringComparison.Ordinal);
            Assert.True(missingAt >= 0, run.Stderr);
            Assert.Contains("ct_001.dcm (InstanceNumber 2): ImagePositionPatient unparseable", run.Stderr);
            // The reduced series has gaps of 2 and 1 mm, so the spacing warning still fires, and
            // it comes after the line that explains the hole it is describing.
            Assert.True(spacingAt > missingAt, run.Stderr);
        }

        [Fact]
        public void MissingPositionMessage_NamesEverySkippedSlice()
        {
            string msg = SeriesGeometryProbe.BuildMissingPositionMessage("1.2.3", 2, 10,
                new[] { "a.dcm (InstanceNumber 4): ImagePositionPatient missing", "b.dcm (InstanceNumber 7): ImagePositionPatient unparseable (\"x\")" });

            Assert.StartsWith("WARNING: series 1.2.3: 2 of 10 image slice(s) carry no usable ImagePositionPatient", msg);
            Assert.Contains("a.dcm (InstanceNumber 4)", msg);
            Assert.Contains("b.dcm (InstanceNumber 7)", msg);
            Assert.Contains("The remaining 8 slice(s)", msg);
            Assert.False(SeriesGeometryProbe.TryBuildMissingPositionWarning(new DicomSeriesGroup(), out _));
        }
    }
}
