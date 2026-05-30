using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Regression tests for C1: an image series' badge/modality must reflect the majority
    /// of its instances, not whichever file won the parallel scan race. (KW saw an MR series
    /// labeled [CT].)
    /// </summary>
    public class DicomScannerModalityTests
    {
        [Fact]
        public async Task SeriesModality_ResolvesToMajority_WhenOneInstanceIsMistagged()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string series = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                // A stray CT-tagged instance sorts first alphabetically (so it would win the
                // legacy first-file-wins behaviour); the four real slices are MR.
                DicomTestData.WriteImageSlice(dir, "0000_stray.dcm", "CT", "P1", study, series, frame, 0.0);
                for (int i = 1; i <= 4; i++)
                    DicomTestData.WriteImageSlice(dir, $"{i:0000}_mr.dcm", "MR", "P1", study, series, frame, i * 3.0);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var s = FindSeries(result, series);
                Assert.NotNull(s);
                Assert.Equal("MR", s.Modality);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task SeriesModality_StaysCt_ForACleanCtSeries()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string series = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"{i:0000}_ct.dcm", "CT", "P1", study, series, frame, i * 2.5);

                var result = await new DicomScannerService()
                    .ScanFolderAsync(dir, null, CancellationToken.None);

                var s = FindSeries(result, series);
                Assert.NotNull(s);
                Assert.Equal("CT", s.Modality);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        private static DicomSeriesGroup FindSeries(DicomScanResult result, string seriesUid)
        {
            return result.Patients
                .SelectMany(p => p.Studies)
                .SelectMany(st => st.Series)
                .FirstOrDefault(se => se.SeriesInstanceUID == seriesUid);
        }
    }
}
