using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// ConvertStructToNifti derived each mask's file name from the ROI name in isolation, inside a
    /// Parallel.ForEach, with no check that the name was still free. Sanitizing is many-to-one, so
    /// an RTSTRUCT carrying "PTV:1", "PTV*1" and "PTV?1" produced one PTV_1.nii.gz written by three
    /// threads at once, three volumes reported against that one path, and two ROIs missing from the
    /// export with nothing said about it.
    ///
    /// The dose path in the same file already suffixes repeated names; these pin the ROI path onto
    /// the same convention, and pin the property that makes it usable — that every caller which
    /// needs to name the same files computes the same answer.
    /// </summary>
    public class MaskFileNamingTests
    {
        private static readonly string[] CollidingRoiNames = { "PTV:1", "PTV*1", "PTV?1" };

        [Fact]
        public void CollidingRoiNames_GetDistinctFileNames()
        {
            var names = NiftiConversionService.BuildUniqueMaskFileNames(CollidingRoiNames);

            Assert.Equal(3, names.Count);
            Assert.Equal(3, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            // One of them keeps the undecorated name; the others are suffixed, as the dose path does.
            Assert.Contains("PTV_1", names.Values);
            Assert.Contains("PTV_1_2", names.Values);
            Assert.Contains("PTV_1_3", names.Values);
        }

        [Fact]
        public void NonCollidingRoiNames_AreLeftAlone()
        {
            var names = NiftiConversionService.BuildUniqueMaskFileNames(new[] { "Lung_L", "Lung_R", "Cord" });

            Assert.Equal("Lung_L", names["Lung_L"]);
            Assert.Equal("Lung_R", names["Lung_R"]);
            Assert.Equal("Cord", names["Cord"]);
        }

        /// <summary>
        /// The writer, the CLI's stdout summary and the cohort manifest all name the same files
        /// from separate dictionaries whose enumeration order is not guaranteed to agree. If the
        /// suffixes depended on arrival order they would disagree, and the manifest would point at
        /// a file some other ROI's mask is in — worse than the collision it replaced.
        /// </summary>
        [Fact]
        public void Assignment_DoesNotDependOnInputOrder()
        {
            var forward = NiftiConversionService.BuildUniqueMaskFileNames(CollidingRoiNames);
            var reversed = NiftiConversionService.BuildUniqueMaskFileNames(
                Enumerable.Reverse(CollidingRoiNames));

            foreach (var roi in CollidingRoiNames)
                Assert.Equal(forward[roi], reversed[roi]);
        }

        [Fact]
        public void DisambiguationIsCaseInsensitive_BecauseTheFilesystemIs()
        {
            var names = NiftiConversionService.BuildUniqueMaskFileNames(new[] { "PTV_1", "ptv:1" });

            Assert.NotEqual(
                names["PTV_1"].ToLowerInvariant(),
                names["ptv:1"].ToLowerInvariant());
        }

        /// <summary>
        /// End to end through the real rasterizer and writer: three ROIs whose names collide after
        /// sanitization must produce three mask files and three matching volumes. Before the fix
        /// this found one file on disk.
        /// </summary>
        [Fact]
        public void ConvertStructToNifti_WritesOneFilePerCollidingRoi()
        {
            string root = DicomTestData.NewTempDir();
            string ctDir = Path.Combine(root, "ct");
            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(outDir);

            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();

            DicomTestData.WriteCtSeriesWithPixels(ctDir, seriesUid, frameUid, studyUid: studyUid);

            // Three distinct ROIs, three distinct shapes, one file name between them.
            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("PTV:1", DicomTestData.SquareContour(2, 6, 1)),
                new KeyValuePair<string, double[]>("PTV*1", DicomTestData.SquareContour(2, 8, 1)),
                new KeyValuePair<string, double[]>("PTV?1", DicomTestData.SquareContour(2, 10, 1)),
            };
            string rtstructPath = DicomTestData.WriteRtStructWithContours(
                root, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), frameUid, contours);

            var volumes = new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                rtStructSeries: RtStructSeries(rtstructPath, frameUid, contours.Select(c => c.Key)),
                imageSeries: ImageSeries(ctDir, seriesUid, frameUid),
                outputDir: outDir,
                associations: null,
                exportUnmatched: true,
                flatOutput: true,
                progress: null,
                ct: CancellationToken.None);

            Assert.Equal(3, volumes.Count);

            var written = Directory.GetFiles(outDir, "*.nii.gz").Select(Path.GetFileName).ToList();
            Assert.Equal(3, written.Count);

            // And the reported name -> file mapping has to agree with what is on disk, or the
            // manifest points at the wrong mask.
            var names = NiftiConversionService.BuildUniqueMaskFileNames(volumes.Keys);
            foreach (var roi in volumes.Keys)
                Assert.Contains(names[roi] + ".nii.gz", written);

            // Different shapes must not have collapsed into one another.
            Assert.Equal(3, volumes.Values.Distinct().Count());
        }

        private static DicomSeriesGroup ImageSeries(string ctDir, string seriesUid, string frameUid)
        {
            return new DicomSeriesGroup
            {
                SeriesInstanceUID = seriesUid,
                Modality = "CT",
                FrameOfReferenceUID = frameUid,
                FilePaths = Directory.GetFiles(ctDir, "*.dcm").OrderBy(p => p, StringComparer.Ordinal).ToList(),
            };
        }

        private static DicomSeriesGroup RtStructSeries(
            string path, string frameUid, IEnumerable<string> roiNames)
        {
            var series = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { path },
            };
            foreach (var n in roiNames) series.RoiNames.Add(n);
            return series;
        }
    }
}
