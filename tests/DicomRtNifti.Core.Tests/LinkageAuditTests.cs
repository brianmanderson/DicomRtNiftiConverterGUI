using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// <see cref="LinkageAudit"/> over the tree the scanner's fingerprint is pinned on, which
    /// exercises every link rule for structure sets and doses, a dose shared by two series, and RT
    /// objects in a study with no images.
    /// </summary>
    public class LinkageAuditTests
    {
        [Fact]
        public async Task TheAudit_ReportsEveryRuleAndEveryLooseEnd()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                DicomScannerGroupingFingerprintTests.WriteTree(root);
                var scan = await new DicomScannerService().ScanFolderAsync(root, null, CancellationToken.None);

                var linkage = LinkageAudit.Audit(scan);

                Assert.Equal(new[] { 1, 1, 1, 1 }, linkage.RuleCounts["RTSTRUCT"].Values);
                Assert.Equal(new[] { "ReferencedSeriesUid", "FrameOfReferenceUid", "LargestSeriesFallback", "None" },
                    linkage.RuleCounts["RTSTRUCT"].Keys);
                Assert.Equal(new[] { 0, 1, 1, 1 }, linkage.RuleCounts["RTDOSE"].Values);

                Assert.Equal(new[] { "1.9.2.3", "1.9.2.5" }, linkage.Fallback.Select(f => f.ObjectUid));
                Assert.All(linkage.Fallback, f => Assert.Equal(new[] { "1.9.2.1" }, f.LinkedTo));

                Assert.Equal(new[] { "1.9.3.3", "1.9.3.5" }, linkage.Unlinked.Select(u => u.ObjectUid));
                Assert.All(linkage.Unlinked, u => Assert.Equal("no image series in study", u.Reason));

                var shared = Assert.Single(linkage.SharedDoses);
                Assert.Equal("1.9.1.5", shared.ObjectUid);
                Assert.Equal(new[] { "1.9.1.1", "1.9.1.2" }, shared.LinkedTo);

                Assert.Empty(linkage.ForMismatch);
                Assert.Empty(linkage.ReferencedSeriesAbsent);
                Assert.Empty(linkage.MultiStructSeries);
                Assert.Empty(linkage.OtherObjects);
                Assert.Equal(1, linkage.SeriesWithoutRtstruct);
                Assert.Equal(1, linkage.SeriesWithoutRtdose);

                Assert.Equal(new[] { "1.9.1", "1.9.2", "1.9.4" }, linkage.FramesPerStudy.Select(f => f.StudyUid));
                Assert.Equal(new[] { 2, 1, 1 }, linkage.FramesPerStudy.Select(f => f.ImageSeries));
                Assert.All(linkage.FramesPerStudy, f => Assert.Equal(1, f.Frames));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void APlanOrAnyOtherObject_IsListed_NotCountedOrFlagged()
        {
            var study = CohortAnalysisFlagTests.Study(CohortAnalysisFlagTests.Ct("8.1"));
            study.Series.Add(new DicomSeriesGroup { SeriesInstanceUID = "8.1.7", Modality = "RTPLAN", SeriesDescription = "plan" });

            var analysis = CohortAnalysisFlagTests.Analyze(study);

            var other = Assert.Single(analysis.Linkage.OtherObjects);
            Assert.Equal("RTPLAN", other.Modality);
            Assert.Equal(1, analysis.Summary.OtherObjects);
            Assert.DoesNotContain(analysis.Flags, f => f.ObjectUid == "8.1.7");
            Assert.All(analysis.Linkage.RuleCounts.Values, counts => Assert.Equal(0, counts.Values.Sum()));
        }
    }
}
