using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Structure-set linking, the RTSTRUCT counterpart to <see cref="DicomScannerDoseLinkingTests"/>.
    /// A study with more than one RTSTRUCT must link all of them; the legacy single reference
    /// assigned rather than appended and so dropped all but the last.
    ///
    /// The shapes here are modelled on the Pancreatic-CT-CBCT-SEG collection, where every patient
    /// is one study holding a planning CT, several CBCTs resampled onto the planning grid, and one
    /// structure set per image series — all sharing a frame of reference.
    /// </summary>
    public class DicomScannerStructLinkingTests
    {
        private static DicomSeriesGroup SeriesByUid(DicomScanResult result, string uid) =>
            result.Patients
                .SelectMany(p => p.Studies)
                .SelectMany(s => s.Series)
                .First(se => se.SeriesInstanceUID == uid);

        [Fact]
        public async Task AllStructureSetsLinked_WhenStudyHasMultipleRtStruct()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 2.5);

                // Three structure sets sharing the CT's frame of reference, as the pancreas
                // collection ships them (one planning-CT set plus per-fraction CBCT sets).
                // Labels are VR SH (16 chars); the collection's longer BSPC_/BSCB_ strings live
                // in SeriesDescription, so keep these short but recognisably planning vs CBCT.
                DicomTestData.WriteRtStruct(dir, "rs_a.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "BSPC_ROI_SDPC", new[] { "Pancreas" });
                DicomTestData.WriteRtStruct(dir, "rs_b.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "BSCB_LL_LR_SDCB", new[] { "Pancreas" });
                DicomTestData.WriteRtStruct(dir, "rs_c.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "BSCB_LL_LR_SDCB", new[] { "Pancreas" });

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = SeriesByUid(result, ctSeries);

                Assert.Equal(3, ct.LinkedRtStructs.Count);
                // Back-compat: the singular reference is still populated, pointing at the first.
                Assert.NotNull(ct.LinkedRtStruct);
                Assert.Same(ct.LinkedRtStructs[0], ct.LinkedRtStruct);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task SingleStructLinked_RemainsConsistentAcrossSingularAndList()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 2.5);
                DicomTestData.WriteRtStruct(dir, "rs.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "structures", new[] { "Pancreas" });

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = SeriesByUid(result, ctSeries);

                Assert.Single(ct.LinkedRtStructs);
                Assert.Same(ct.LinkedRtStructs[0], ct.LinkedRtStruct);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task ReferencedSeriesUid_WinsOverFrameOfReference()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string planningCt = DicomTestData.NewUid();
                string alignedCbct = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                // Both image series share one frame of reference — the CBCT is resampled onto
                // the planning grid. Only the referenced-SeriesInstanceUID can tell them apart.
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"pct_{i}.dcm", "CT", "P1", study, planningCt, frame, i * 2.5);
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"cbct_{i}.dcm", "CT", "P1", study, alignedCbct, frame, i * 2.5);

                // Points explicitly at the CBCT, which is NOT the first image series in the study.
                DicomTestData.WriteRtStruct(dir, "rs_cbct.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "BSCB_SDCB", new[] { "Pancreas" }, referencedSeriesUid: alignedCbct);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var cbct = SeriesByUid(result, alignedCbct);
                var pct = SeriesByUid(result, planningCt);

                Assert.Single(cbct.LinkedRtStructs);
                Assert.Empty(pct.LinkedRtStructs);
                Assert.Equal(RtLinkMatchRule.ReferencedSeriesUid, cbct.LinkedRtStructs[0].LinkMatchRule);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task FrameOfReferenceMatch_IsReportedAsSuch()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 2.5);
                DicomTestData.WriteRtStruct(dir, "rs.dcm", "P1", study, DicomTestData.NewUid(), frame,
                    "structures", new[] { "Pancreas" });

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = SeriesByUid(result, ctSeries);
                Assert.Equal(RtLinkMatchRule.FrameOfReferenceUid, ct.LinkedRtStructs[0].LinkMatchRule);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task DoseLinksToEverySeriesSharingItsFrameOfReference()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string planningCt = DicomTestData.NewUid();
                string cbct = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                // A planning CT and a CBCT registered onto it, sharing one frame of reference.
                // An RT-DOSE names no image series, so the frame is all there is to go on — and
                // it cannot discriminate. Picking one would be a coin flip that orphans the dose
                // whenever the caller selects the other; sharing a frame means sharing a patient
                // coordinate system, so the dose is spatially valid for both.
                for (int i = 0; i < 20; i++)
                    DicomTestData.WriteImageSlice(dir, $"pct_{i:00}.dcm", "CT", "P1", study, planningCt, frame, i * 3.0);
                for (int i = 0; i < 20; i++)
                    DicomTestData.WriteImageSlice(dir, $"cb_{i:00}.dcm", "CT", "P1", study, cbct, frame, i * 3.0);

                DicomTestData.WriteRtDose(dir, "dose.dcm", "P1", study, DicomTestData.NewUid(), frame);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                Assert.Single(SeriesByUid(result, planningCt).LinkedRtDoses);
                Assert.Single(SeriesByUid(result, cbct).LinkedRtDoses);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task DoseWithReferencedSeriesUid_LinksToThatSeriesOnly()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string a = DicomTestData.NewUid();
                string b = DicomTestData.NewUid();
                string frameA = DicomTestData.NewUid();
                string frameB = DicomTestData.NewUid();

                // Distinct frames of reference: the dose matches exactly one, so the broad
                // frame-sharing rule must not spill it onto the other series.
                for (int i = 0; i < 10; i++)
                    DicomTestData.WriteImageSlice(dir, $"a_{i:00}.dcm", "CT", "P1", study, a, frameA, i * 3.0);
                for (int i = 0; i < 10; i++)
                    DicomTestData.WriteImageSlice(dir, $"b_{i:00}.dcm", "CT", "P1", study, b, frameB, i * 3.0);

                DicomTestData.WriteRtDose(dir, "dose.dcm", "P1", study, DicomTestData.NewUid(), frameB);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                Assert.Empty(SeriesByUid(result, a).LinkedRtDoses);
                Assert.Single(SeriesByUid(result, b).LinkedRtDoses);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task LargestSeriesFallback_IsReportedAsSuch()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries,
                        DicomTestData.NewUid(), i * 2.5);

                // Unrelated frame of reference and no referenced series: only the positional
                // fallback can match, and callers need to know the link is a guess.
                DicomTestData.WriteRtStruct(dir, "rs.dcm", "P1", study, DicomTestData.NewUid(),
                    DicomTestData.NewUid(), "structures", new[] { "Pancreas" });

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = SeriesByUid(result, ctSeries);
                Assert.Single(ct.LinkedRtStructs);
                Assert.Equal(RtLinkMatchRule.LargestSeriesFallback, ct.LinkedRtStructs[0].LinkMatchRule);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task StructAndDoseBothLink_OnPancreasShapedStudy()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();
                string planningCt = DicomTestData.NewUid();

                // One planning CT plus four CBCTs, all one study, all one frame of reference:
                // the shape every Pancreatic-CT-CBCT-SEG patient has.
                for (int i = 0; i < 5; i++)
                    DicomTestData.WriteImageSlice(dir, $"pct_{i}.dcm", "CT", "P1", study, planningCt, frame, i * 2.5);
                for (int c = 0; c < 4; c++)
                {
                    string cbct = DicomTestData.NewUid();
                    for (int i = 0; i < 5; i++)
                        DicomTestData.WriteImageSlice(dir, $"cb{c}_{i}.dcm", "CT", "P1", study, cbct, frame, i * 2.5);
                }

                for (int s = 0; s < 3; s++)
                    DicomTestData.WriteRtStruct(dir, $"rs_{s}.dcm", "P1", study, DicomTestData.NewUid(), frame,
                        "structures", new[] { "Pancreas", "Duodenum" });
                DicomTestData.WriteRtDose(dir, "dose.dcm", "P1", study, DicomTestData.NewUid(), frame);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var allSeries = result.Patients
                    .SelectMany(p => p.Studies)
                    .SelectMany(s => s.Series)
                    .ToList();

                // Nothing is dropped: all three structure sets survive the scan.
                Assert.Equal(3, allSeries.Sum(s => s.LinkedRtStructs.Count));

                // The single dose reaches all five image series, because all five share its
                // frame of reference and nothing in the dose says which one it was computed on.
                Assert.Equal(5, allSeries.Count(s => s.LinkedRtDoses.Count == 1));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
