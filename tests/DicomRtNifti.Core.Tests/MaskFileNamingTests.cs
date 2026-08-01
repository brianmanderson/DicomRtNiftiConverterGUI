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
            // All three start from the same sanitized stem and are separated by a digest of their
            // own ROI name. None of them keeps the undecorated "PTV_1": handing that out to
            // whichever ROI sorted first is what made the name depend on the selection, and it is
            // the property these tests used to pin.
            Assert.All(names.Values, v => Assert.StartsWith("PTV_1_", v));
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

        /// <summary>
        /// The failure order-independence alone did not cover. Suffixes were handed out by rank in
        /// an ordinal sort of the *selected* ROIs, so deselecting one renamed another: exporting
        /// {"GTV:1", "GTV*1"} put "GTV:1" in GTV_1_2, and re-exporting just {"GTV:1"} put it in
        /// GTV_1 — while the stale GTV_1_2 from the first run stayed on disk, unpruned. Anyone
        /// holding the first run's manifest then read GTV_1_2 and got the wrong ROI's mask, with
        /// nothing to distinguish it from a correct read.
        /// </summary>
        [Fact]
        public void AFileName_DoesNotChange_WhenAnotherRoiIsDeselected()
        {
            var wide = NiftiConversionService.BuildUniqueMaskFileNames(new[] { "GTV:1", "GTV*1" });
            var narrow = NiftiConversionService.BuildUniqueMaskFileNames(new[] { "GTV:1" });

            Assert.Equal(wide["GTV:1"], narrow["GTV:1"]);

            // And the file the deselected ROI had is not handed to anybody else.
            Assert.NotEqual(wide["GTV*1"], narrow["GTV:1"]);
        }

        /// <summary>
        /// The same property for a name that needed no sanitizing: adding a colliding sibling must
        /// not move it either.
        /// </summary>
        [Fact]
        public void AValidRoiName_KeepsItsOwnFileName_WhateverElseIsSelected()
        {
            var alone = NiftiConversionService.BuildUniqueMaskFileNames(new[] { "GTV_1" });
            var withSibling = NiftiConversionService.BuildUniqueMaskFileNames(
                new[] { "GTV_1", "GTV:1", "GTV*1" });

            Assert.Equal("GTV_1", alone["GTV_1"]);
            Assert.Equal("GTV_1", withSibling["GTV_1"]);
            Assert.Equal(3, withSibling.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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

        /// <summary>
        /// The manifest-level consequence, through the real writer and into one output root: run 1
        /// exports two ROIs whose names collide after sanitization, run 2 exports only one of them
        /// into the same folder. Whatever run 1's manifest said about the ROI both runs covered has
        /// to still be true afterwards — before the fix run 2 rewrote that ROI under run 1's *other*
        /// ROI's file name, so the earlier manifest silently resolved to the wrong mask.
        /// </summary>
        [Fact]
        public void ANarrowerReExport_LeavesTheEarlierManifestPointingAtTheSameRoi()
        {
            string root = DicomTestData.NewTempDir();
            string ctDir = Path.Combine(root, "ct");
            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(outDir);

            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, seriesUid, frameUid, studyUid: studyUid);

            // "GTV:1" is a cube, "GTV*1" a wider square; both sanitize to GTV_1.
            var cube = new KeyValuePair<string, double[]>("GTV:1", DicomTestData.SquareContour(2, 6, 1));
            var wide = new KeyValuePair<string, double[]>("GTV*1", DicomTestData.SquareContour(2, 12, 1));

            var service = new NiftiConversionService(new RtStructMaskService());

            string bothPath = DicomTestData.WriteRtStructWithContours(
                root, "both.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>> { cube, wide });
            var runOne = service.ConvertStructToNifti(
                RtStructSeries(bothPath, frameUid, new[] { cube.Key, wide.Key }),
                ImageSeries(ctDir, seriesUid, frameUid),
                outDir, null, true, false, null, CancellationToken.None);

            // What run 1's manifest recorded for the ROI that both runs cover.
            string recordedFile = Path.Combine(outDir, "masks",
                NiftiConversionService.BuildUniqueMaskFileNames(runOne.Keys)[cube.Key] + ".nii.gz");
            byte[] recordedBytes = File.ReadAllBytes(recordedFile);
            double recordedVolume = runOne[cube.Key];

            string cubeOnlyPath = DicomTestData.WriteRtStructWithContours(
                root, "cube.dcm", studyUid, DicomTestData.NewUid(), frameUid,
                new List<KeyValuePair<string, double[]>> { cube });
            var runTwo = service.ConvertStructToNifti(
                RtStructSeries(cubeOnlyPath, frameUid, new[] { cube.Key }),
                ImageSeries(ctDir, seriesUid, frameUid),
                outDir, null, true, false, null, CancellationToken.None);

            // Same ROI, same file, same bytes: the narrower run rewrote its own output and left
            // the file the earlier manifest names holding the ROI it named.
            Assert.Equal(recordedFile,
                Path.Combine(outDir, "masks",
                    NiftiConversionService.BuildUniqueMaskFileNames(runTwo.Keys)[cube.Key] + ".nii.gz"));
            Assert.Equal(recordedVolume, runTwo[cube.Key], 6);
            Assert.Equal(recordedBytes, File.ReadAllBytes(recordedFile));
        }

        /// <summary>
        /// The stale file run 2 leaves behind is still named, so "the folder is a complete
        /// description of the last run" is not silently untrue.
        /// </summary>
        [Fact]
        public void ANarrowerReExport_NamesTheMasksItDidNotWrite()
        {
            string root = DicomTestData.NewTempDir();
            string ctDir = Path.Combine(root, "ct");
            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(Path.Combine(outDir, "masks"));

            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, seriesUid, frameUid, studyUid: studyUid);

            File.WriteAllBytes(Path.Combine(outDir, "masks", "Left_Over.nii.gz"), new byte[] { 0x1f, 0x8b });

            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("Cord", DicomTestData.SquareContour(2, 6, 1)),
            };
            string rtstructPath = DicomTestData.WriteRtStructWithContours(
                root, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), frameUid, contours);

            var log = new CollectingProgress();
            new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                RtStructSeries(rtstructPath, frameUid, new[] { "Cord" }),
                ImageSeries(ctDir, seriesUid, frameUid),
                outDir, null, true, false, log, CancellationToken.None);

            Assert.Contains(log.Messages,
                m => m.Contains("Left_Over.nii.gz") && m.Contains("not written by this export"));
            Assert.True(File.Exists(Path.Combine(outDir, "masks", "Left_Over.nii.gz")),
                "the stale mask is reported, not deleted — it is the caller's data");
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
