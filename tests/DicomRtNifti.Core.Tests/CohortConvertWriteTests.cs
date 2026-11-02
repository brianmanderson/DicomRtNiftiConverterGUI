using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The cohort conversion was made faster the way the survey was: each series' CT loaded once
    /// for its image and its masks, and two series converted at once. Neither may change a file.
    /// These pin both against the paths they replaced.
    /// </summary>
    public class CohortConvertWriteTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// ConvertSeries writes the files ConvertImageSeriesToNifti and ConvertStructToNifti write
        /// separately, byte for byte, on a mixed-gap series with scrambled file order, native and
        /// resampled.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConvertSeries_WritesWhatTheSeparateCallsWrite(bool resample)
        {
            string ctDir = Path.Combine(_dir, "ct");
            string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, DicomTestData.NewUid(), frame,
                size: 24, studyUid: study, sliceZsMm: new[] { 0.0, 2.0, 4.0, 10.0, 12.0, 20.0 });
            string rtPath = DicomTestData.WriteRtStructWithContours(Path.Combine(_dir, "rt"), "rs.dcm",
                study, DicomTestData.NewUid(), frame, new[]
                {
                    new KeyValuePair<string, double[]>("Small", DicomTestData.SquareContour(3, 9, 2)),
                    new KeyValuePair<string, double[]>("Far", DicomTestData.SquareContour(5, 17, 12)),
                });

            var files = Directory.GetFiles(ctDir, "*.dcm").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
            var image = new DicomSeriesGroup { Modality = "CT", FilePaths = files };
            var rtStruct = new DicomSeriesGroup { Modality = "RTSTRUCT", FilePaths = { rtPath }, RoiNames = { "Small", "Far" } };
            double[] target = resample ? new[] { 2.0, 2.0, 3.0 } : null;
            var service = new NiftiConversionService(new RtStructMaskService());

            string separate = Path.Combine(_dir, "separate");
            Directory.CreateDirectory(separate);
            double[] separateSpacing = service.ConvertImageSeriesToNifti(image, separate, null, CancellationToken.None, target);
            var separateVolumes = service.ConvertStructToNifti(rtStruct, image, separate, null, true, false, null, CancellationToken.None, target);

            string combined = Path.Combine(_dir, "combined");
            Directory.CreateDirectory(combined);
            var conversion = service.ConvertSeries(image, combined, true, rtStruct, null, true, false, null, CancellationToken.None, target);

            Assert.Equal(separateSpacing, conversion.Spacing);
            Assert.Equal(separateVolumes.OrderBy(k => k.Key), conversion.RoiVolumes.OrderBy(k => k.Key));
            Assert.All(conversion.RoiVolumes.Values, v => Assert.True(v > 0));
            var expected = HashTree(separate);
            Assert.Equal(3, expected.Count); // image + two masks
            Assert.Equal(expected, HashTree(combined));

            // Without the image: the series' own spacing, the same masks, no image file.
            string masksOnly = Path.Combine(_dir, "masksOnly");
            Directory.CreateDirectory(masksOnly);
            var noImage = service.ConvertSeries(image, masksOnly, false, rtStruct, null, true, false, null, CancellationToken.None, target);
            Assert.Equal(service.GetImageSpacing(image), noImage.Spacing);
            Assert.Equal(expected.Where(k => k.Key.StartsWith("masks/", StringComparison.Ordinal)), HashTree(masksOnly));
        }

        /// <summary>
        /// Several series at once write what one at a time writes: every file byte for byte, the
        /// manifest, the series and errors in plan order, and the log. One structure set is damaged
        /// so a failure sits mid-plan.
        /// </summary>
        [Fact]
        public void ParallelConvert_WritesWhatASequentialConvertWrites()
        {
            string input = WriteCohort();

            var sequential = Convert(input, "seq", parallelism: 1, failFast: false);
            var parallel = Convert(input, "par", parallelism: 3, failFast: false);

            Assert.Null(sequential.Thrown);
            Assert.Null(parallel.Thrown);
            Assert.Equal(4, sequential.Result.SucceededCount);
            Assert.Equal("PAT_C", Assert.Single(sequential.Result.Errors).PatientId);
            Assert.Contains(sequential.Files.Keys, k => k.EndsWith("/image.nii.gz", StringComparison.Ordinal));
            Assert.Contains(sequential.Files.Keys, k => k.EndsWith("/masks/Target.nii.gz", StringComparison.Ordinal));

            Assert.Equal(sequential.Files, parallel.Files);
            Assert.Equal(sequential.Result.Series.Select(s => s.PatientId + " " + s.Image + " " + string.Join(",", s.Masks.Select(m => m.File + "=" + m.VolumeCc.ToString("R")))),
                         parallel.Result.Series.Select(s => s.PatientId + " " + s.Image + " " + string.Join(",", s.Masks.Select(m => m.File + "=" + m.VolumeCc.ToString("R")))));
            Assert.Equal(sequential.Result.Errors.Select(e => e.PatientId + ": " + e.Message),
                         parallel.Result.Errors.Select(e => e.PatientId + ": " + e.Message));
            Assert.Equal(sequential.Log, parallel.Log);
        }

        /// <summary>
        /// A survey always runs series at once; a conversion does unless --fail-fast is set or the
        /// metadata sidecars are anonymized.
        /// </summary>
        [Fact]
        public void ConversionStaysOneAtATime_UnderFailFast_AndAnonymizedMetadata()
        {
            var plan = new CohortExportPlan();
            plan.Series.Add(new PlannedSeries());
            plan.Series.Add(new PlannedSeries());
            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
            {
                SeriesParallelism = 2,
            };

            Assert.True(service.RunsSeriesAtOnce(plan, new CohortExportOptions(), writeFiles: true));
            Assert.True(service.RunsSeriesAtOnce(plan, new CohortExportOptions { FailFast = true }, writeFiles: false));
            Assert.False(service.RunsSeriesAtOnce(plan, new CohortExportOptions { FailFast = true }, writeFiles: true));

            var anonymizedMetadata = new CohortExportOptions { Anonymize = true, ImageKeywords = { "PatientID" } };
            Assert.True(anonymizedMetadata.HasMetadataSelections);
            Assert.False(service.RunsSeriesAtOnce(plan, anonymizedMetadata, writeFiles: true));
            Assert.True(service.RunsSeriesAtOnce(plan, anonymizedMetadata, writeFiles: false));
            Assert.True(service.RunsSeriesAtOnce(plan, new CohortExportOptions { Anonymize = true }, writeFiles: true));
            Assert.True(service.RunsSeriesAtOnce(plan, new CohortExportOptions { ImageKeywords = { "PatientID" } }, writeFiles: true));

            service.SeriesParallelism = 1;
            Assert.False(service.RunsSeriesAtOnce(plan, new CohortExportOptions(), writeFiles: false));
        }

        private string WriteCohort()
        {
            string input = Path.Combine(_dir, "cohort");
            foreach (var id in new[] { "PAT_A", "PAT_B", "PAT_C", "PAT_D", "PAT_E" })
                DicomTestData.WritePatientWithStructureSet(input, id, damagedContours: id == "PAT_C");
            return input;
        }

        private sealed class ConvertRun
        {
            public CohortExportResult Result;
            public Exception Thrown;
            public Dictionary<string, string> Files;
            public List<string> Log;
        }

        private ConvertRun Convert(string input, string name, int parallelism, bool failFast)
        {
            var scan = new DicomScannerService()
                .ScanFolderAsync(input, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            string output = Path.Combine(_dir, name);
            var options = new CohortExportOptions
            {
                InputRoot = input,
                OutputRoot = output,
                FailFast = failFast,
            };
            var plan = CohortExportService.BuildPlan(scan, options, null);
            Assert.Equal(5, plan.Series.Count);

            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
            {
                SeriesParallelism = parallelism,
            };
            var log = new CollectingProgress();
            var run = new ConvertRun();
            try
            {
                run.Result = service.ExecuteAsync(plan, options, null, log, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                run.Thrown = ex;
            }

            run.Files = HashTree(output);
            string root = Path.GetFullPath(output);
            run.Log = log.Messages.Select(m => m.Replace(root, "<ROOT>")).ToList();
            return run;
        }

        /// <summary>Relative path (forward slashes) to SHA-256 of every file under <paramref name="root"/>.</summary>
        private static Dictionary<string, string> HashTree(string root)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root))
                return hashes;
            using (var sha = SHA256.Create())
            {
                foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                    hashes[rel] = System.Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
                }
            }
            return hashes;
        }
    }
}
