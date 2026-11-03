using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The manifest survey was made faster by loading each series once, taking the per-slice Z
    /// from the sort's header pass, counting mask voxels from the buffer, and surveying two series
    /// at once. None of that may move a number or reorder a row; these pin each piece against the
    /// path it replaced.
    /// </summary>
    public class CohortManifestSurveyTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void SumMaskVoxels_IsTheStatisticsFilterSum()
        {
            var random = new Random(20261102);

            // Binary, and more than one 1 MiB chunk so the chunk boundary is crossed.
            using (var binary = new Image(131, 97, 89, PixelIDValueEnum.sitkUInt8))
            {
                var bytes = new byte[131 * 97 * 89];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(random.Next(7) == 0 ? 1 : 0);
                Marshal.Copy(bytes, 0, binary.GetBufferAsUInt8(), bytes.Length);
                Assert.Equal(FilterSum(binary), NiftiConversionService.SumMaskVoxels(binary));
            }

            // Not binary: summed value by value, still the filter's number.
            using (var multi = new Image(64, 64, 9, PixelIDValueEnum.sitkUInt8))
            {
                var bytes = new byte[64 * 64 * 9];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)random.Next(6);
                Marshal.Copy(bytes, 0, multi.GetBufferAsUInt8(), bytes.Length);
                Assert.Equal(FilterSum(multi), NiftiConversionService.SumMaskVoxels(multi));
            }

            // Another pixel type goes through the filter itself.
            using (var real = new Image(8, 8, 3, PixelIDValueEnum.sitkFloat32))
            {
                real.SetPixelAsFloat(new VectorUInt32(new uint[] { 1, 2, 0 }), 0.25f);
                real.SetPixelAsFloat(new VectorUInt32(new uint[] { 7, 7, 2 }), 3.5f);
                Assert.Equal(FilterSum(real), NiftiConversionService.SumMaskVoxels(real));
            }
        }

        /// <summary>
        /// SurveySeries against the two-load path it replaces, rebuilt here from the loader, the
        /// per-slice Z read and the rasterizer, on a mixed-gap series: the case where the per-slice Z
        /// decides which slice a contour lands on. File order is scrambled so the sort has work.
        /// </summary>
        [Fact]
        public void SurveySeries_GivesTheNumbersOfTheSeparateLoads()
        {
            string ctDir = Path.Combine(_dir, "ct");
            string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            double[] zs = { 0.0, 2.0, 4.0, 10.0, 12.0, 20.0 };
            DicomTestData.WriteCtSeriesWithPixels(ctDir, DicomTestData.NewUid(), frame,
                size: 24, studyUid: study, sliceZsMm: zs);
            string rtPath = DicomTestData.WriteRtStructWithContours(Path.Combine(_dir, "rt"), "rs.dcm",
                study, DicomTestData.NewUid(), frame, new[]
                {
                    new KeyValuePair<string, double[]>("Small", DicomTestData.SquareContour(3, 9, 2)),
                    new KeyValuePair<string, double[]>("Far", DicomTestData.SquareContour(5, 17, 12)),
                });

            var nameOrder = Directory.GetFiles(ctDir, "*.dcm").OrderBy(f => f, StringComparer.Ordinal).ToList();
            var image = new DicomSeriesGroup { Modality = "CT", FilePaths = Enumerable.Reverse(nameOrder).ToList() };
            var rtStruct = new DicomSeriesGroup { Modality = "RTSTRUCT", FilePaths = { rtPath }, RoiNames = { "Small", "Far" } };

            var service = new NiftiConversionService(new RtStructMaskService());

            // The per-slice Z the volume pass now takes from the sort is what ReadPerSliceZ reads
            // back from the sorted files, bit for bit.
            var sorted = service.SortFilesBySlicePosition(image.FilePaths, out double[] sortZs);
            Assert.Equal(nameOrder, sorted);
            Assert.Equal(DicomImageSeriesLoader.ReadPerSliceZ(nameOrder), sortZs);

            var survey = service.SurveySeries(image, rtStruct, null, true, null, CancellationToken.None);

            double[] expectedSpacing = service.GetImageSpacing(image);
            var expectedVolumes = new Dictionary<string, double>();
            using (var reference = DicomImageSeriesLoader.LoadCorrected(nameOrder))
            {
                var spacing = reference.GetSpacing();
                double voxelVolume = spacing[0] * spacing[1] * spacing[2];
                var roiNames = new Dictionary<string, string> { ["Small"] = "Small", ["Far"] = "Far" };
                var masks = new RtStructMaskService().RasterizeRois(rtPath, reference, roiNames, null,
                    CancellationToken.None, DicomImageSeriesLoader.ReadPerSliceZ(nameOrder));
                foreach (var kv in masks)
                {
                    expectedVolumes[kv.Key] = FilterSum(kv.Value) * voxelVolume / 1000;
                    kv.Value.Dispose();
                }
            }

            Assert.Equal(expectedSpacing, survey.Spacing);
            Assert.Equal(expectedVolumes.OrderBy(k => k.Key), survey.RoiVolumes.OrderBy(k => k.Key));
            Assert.All(survey.RoiVolumes.Values, v => Assert.True(v > 0));

            // ComputeStructVolumes now shares this path and must still agree, key order included.
            var volumes = service.ComputeStructVolumes(rtStruct, image, null, true, null, CancellationToken.None);
            Assert.Equal(survey.RoiVolumes.ToList(), volumes.ToList());

            // Spacing only: no structure set, no volumes.
            Assert.Null(service.SurveySeries(image, null, null, true, null, CancellationToken.None).RoiVolumes);
        }

        /// <summary>
        /// Two series at once, or any number, record exactly what one at a time does: the manifest
        /// byte for byte, the series and errors in plan order, and the log in plan order with each
        /// series' lines together. One structure set is damaged so a failure sits mid-plan.
        /// </summary>
        [Fact]
        public void ParallelSurvey_RecordsWhatASequentialSurveyRecords()
        {
            string input = WriteCohort();

            var sequential = Survey(input, "seq", parallelism: 1, failFast: false);
            var parallel = Survey(input, "par", parallelism: 3, failFast: false);

            Assert.Null(sequential.Thrown);
            Assert.Null(parallel.Thrown);
            Assert.Equal(4, sequential.Result.SucceededCount);
            Assert.Equal("PAT_C", Assert.Single(sequential.Result.Errors).PatientId);

            Assert.Equal(sequential.Manifest, parallel.Manifest);
            Assert.Equal(sequential.Result.Series.Select(s => s.PatientId), parallel.Result.Series.Select(s => s.PatientId));
            Assert.Equal(sequential.Result.Errors.Select(e => e.PatientId + ": " + e.Message),
                         parallel.Result.Errors.Select(e => e.PatientId + ": " + e.Message));
            Assert.Equal(sequential.Result.RoiColumns, parallel.Result.RoiColumns);
            Assert.Equal(sequential.Log, parallel.Log);
        }

        /// <summary>
        /// --fail-fast stops at the same series with the same exception and the same log, though
        /// the parallel run may already have surveyed the series after it.
        /// </summary>
        [Fact]
        public void ParallelSurvey_FailFast_StopsWhereASequentialSurveyStops()
        {
            string input = WriteCohort();

            var sequential = Survey(input, "seq", parallelism: 1, failFast: true);
            var parallel = Survey(input, "par", parallelism: 3, failFast: true);

            Assert.NotNull(sequential.Thrown);
            Assert.Equal(sequential.Thrown.GetType(), parallel.Thrown?.GetType());
            Assert.Equal(sequential.Thrown.Message, parallel.Thrown.Message);
            Assert.Equal(sequential.Log, parallel.Log);
            Assert.Contains(parallel.Log, m => m.StartsWith("  failed: ", StringComparison.Ordinal));
            Assert.DoesNotContain(parallel.Log, m => m.StartsWith("[4/5]", StringComparison.Ordinal));
        }

        private string WriteCohort()
        {
            string input = Path.Combine(_dir, "cohort");
            foreach (var id in new[] { "PAT_A", "PAT_B", "PAT_C", "PAT_D", "PAT_E" })
                DicomTestData.WritePatientWithStructureSet(input, id, damagedContours: id == "PAT_C");
            return input;
        }

        private sealed class SurveyRun
        {
            public CohortExportResult Result;
            public Exception Thrown;
            public string Manifest;
            public List<string> Log;
        }

        private SurveyRun Survey(string input, string name, int parallelism, bool failFast)
        {
            var scan = new DicomScannerService()
                .ScanFolderAsync(input, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var options = new CohortExportOptions
            {
                InputRoot = input,
                OutputRoot = Path.Combine(_dir, name),
                FailFast = failFast,
            };
            var plan = CohortExportService.BuildPlan(scan, options, null);
            Assert.Equal(5, plan.Series.Count);

            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
            {
                SeriesParallelism = parallelism,
            };
            var log = new CollectingProgress();
            var run = new SurveyRun();
            try
            {
                run.Result = service.ComputeManifestAsync(plan, options, null, log, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                run.Thrown = ex;
            }

            string manifest = Path.Combine(options.OutputRoot, options.ManifestFileName);
            run.Manifest = File.Exists(manifest) ? File.ReadAllText(manifest) : null;
            run.Log = log.Messages;
            return run;
        }

        private static double FilterSum(Image image)
        {
            var stats = new StatisticsImageFilter();
            stats.Execute(image);
            return stats.GetSum();
        }
    }
}
