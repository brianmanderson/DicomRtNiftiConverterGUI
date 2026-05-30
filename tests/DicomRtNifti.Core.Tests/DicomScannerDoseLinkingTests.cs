using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dicom_RT_images_Csharp.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Regression tests for C4: a study with more than one RT-DOSE must link all of them to
    /// the image series. The legacy single reference dropped all but the last dose.
    /// </summary>
    public class DicomScannerDoseLinkingTests
    {
        [Fact]
        public async Task AllDosesLinked_WhenStudyHasMultipleRtDose()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 2.5);

                // Two distinct dose series sharing the CT's frame of reference.
                DicomTestData.WriteRtDose(dir, "dose_a.dcm", "P1", study, DicomTestData.NewUid(), frame);
                DicomTestData.WriteRtDose(dir, "dose_b.dcm", "P1", study, DicomTestData.NewUid(), frame);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = result.Patients
                    .SelectMany(p => p.Studies)
                    .SelectMany(s => s.Series)
                    .First(se => se.SeriesInstanceUID == ctSeries);

                Assert.Equal(2, ct.LinkedRtDoses.Count);
                // Back-compat: the singular reference is still populated (first dose).
                Assert.NotNull(ct.LinkedRtDose);
                Assert.Same(ct.LinkedRtDoses[0], ct.LinkedRtDose);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task SingleDoseLinked_RemainsConsistentAcrossSingularAndList()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 2.5);
                DicomTestData.WriteRtDose(dir, "dose.dcm", "P1", study, DicomTestData.NewUid(), frame);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var ct = result.Patients
                    .SelectMany(p => p.Studies)
                    .SelectMany(s => s.Series)
                    .First(se => se.SeriesInstanceUID == ctSeries);

                Assert.Single(ct.LinkedRtDoses);
                Assert.Same(ct.LinkedRtDoses[0], ct.LinkedRtDose);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
