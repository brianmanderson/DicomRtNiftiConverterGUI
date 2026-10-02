using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// RTSTRUCT has no Frame of Reference module, so real structure sets normally omit the
    /// top-level FrameOfReferenceUID and carry it in ReferencedFrameOfReferenceSequence and on
    /// each ROI. The scanner read only the top-level tag, so for such a structure set the
    /// frame-of-reference link rule never fired and a structure set that named no series fell
    /// through to the largest series in the study, even when another series shared its frame.
    /// </summary>
    public class RtStructFrameOfReferenceLinkingTests
    {
        [Fact]
        public async Task StructureSetWithFrameOnlyOnItsRois_LinksByFrame_NotToTheLargestSeries()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string frameA = DicomTestData.NewUid();
                string frameB = DicomTestData.NewUid();
                string seriesA = DicomTestData.NewUid();
                string seriesB = DicomTestData.NewUid();

                // Series B is larger; the structure set belongs to series A's frame.
                for (int i = 0; i < 4; i++)
                    DicomTestData.WriteImageSlice(dir, $"a_{i}.dcm", "CT", "PAT1", study, seriesA, frameA, i);
                for (int i = 0; i < 9; i++)
                    DicomTestData.WriteImageSlice(dir, $"b_{i}.dcm", "CT", "PAT1", study, seriesB, frameB, i);

                // No referenced series; frame of reference only on the ROI items.
                string rs = DicomTestData.WriteRtStructWithContours(dir, "rs.dcm", study, DicomTestData.NewUid(), frameA,
                    new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double[]>>
                    {
                        new System.Collections.Generic.KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(1, 3, 1)),
                    },
                    patientId: "PAT1");
                var file = DicomFile.Open(rs);
                file.Dataset.Remove(DicomTag.FrameOfReferenceUID);
                file.Save(rs);

                var scan = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
                var studyGroup = scan.Patients.Single().Studies.Single();
                var rtStruct = studyGroup.Series.Single(s => s.Modality == "RTSTRUCT");
                var a = studyGroup.Series.Single(s => s.SeriesInstanceUID == seriesA);
                var b = studyGroup.Series.Single(s => s.SeriesInstanceUID == seriesB);

                Assert.Equal(frameA, rtStruct.FrameOfReferenceUID);
                Assert.Equal(RtLinkMatchRule.FrameOfReferenceUid, rtStruct.LinkMatchRule);
                Assert.Same(rtStruct, a.LinkedRtStruct);
                Assert.Null(b.LinkedRtStruct);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
