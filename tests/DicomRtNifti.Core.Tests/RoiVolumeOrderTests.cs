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
    /// The ROI volumes came back in the enumeration order of a ConcurrentDictionary filled from a
    /// Parallel.ForEach, so two ROIs whose names shared a hash bucket swapped places between
    /// identical runs, and the --forward summary rows with them (toolkit issue 96: on the
    /// conformance fixture, ellipsoid and torus traded places in about half the runs). Names,
    /// volumes and masks never changed; only the order did. These pin the order to the structure
    /// set's, the same for the writer and the volume-only survey, run after run.
    /// </summary>
    public class RoiVolumeOrderTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// The conformance fixture's seven ROIs, in its structure set's order, which is neither
        /// ordinal nor the order the old code produced.
        /// </summary>
        private static readonly string[] FixtureRoiNames =
            { "sphere", "cube", "cylinder", "ellipsoid", "torus", "hollow_sphere", "straw" };

        [Fact]
        public void EveryRun_ReturnsTheStructureSetOrder_AndTheSurveyAgrees()
        {
            var contours = FixtureRoiNames
                .Select((name, i) => new KeyValuePair<string, double[]>(
                    name, DicomTestData.SquareContour(2, 5 + i, 1)))
                .ToList();
            var (image, rtStruct) = WriteCase(contours);
            var service = new NiftiConversionService(new RtStructMaskService());

            for (int run = 0; run < 5; run++)
            {
                string outDir = OutDir("out" + run);
                var written = service.ConvertStructToNifti(rtStruct, image, outDir, null, true, true,
                    null, CancellationToken.None);
                Assert.Equal(FixtureRoiNames, written.Keys);

                var surveyed = service.ComputeStructVolumes(rtStruct, image, null, true, null, CancellationToken.None);
                Assert.Equal(written.ToList(), surveyed.ToList());

                // The cohort paths go through the same two bodies.
                var conversion = service.ConvertSeries(image, OutDir("series" + run), false,
                    rtStruct, null, true, true, null, CancellationToken.None);
                Assert.Equal(written.ToList(), conversion.RoiVolumes.ToList());
                var survey = service.SurveySeries(image, rtStruct, null, true, null, CancellationToken.None);
                Assert.Equal(written.ToList(), survey.RoiVolumes.ToList());
            }
        }

        /// <summary>
        /// A name an association chose takes the place of the DICOM ROI it matched, not the place
        /// of its association in the list.
        /// </summary>
        [Fact]
        public void AnAssociatedName_TakesThePlaceOfTheRoiItMatched()
        {
            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("Cord", DicomTestData.SquareContour(2, 5, 1)),
                new KeyValuePair<string, double[]>("Heart", DicomTestData.SquareContour(2, 6, 1)),
                new KeyValuePair<string, double[]>("Lung-L", DicomTestData.SquareContour(2, 7, 1)),
                new KeyValuePair<string, double[]>("Body", DicomTestData.SquareContour(2, 8, 1)),
                new KeyValuePair<string, double[]>("Esophagus", DicomTestData.SquareContour(2, 9, 1)),
                new KeyValuePair<string, double[]>("Lung-R", DicomTestData.SquareContour(2, 10, 1)),
            };
            var (image, rtStruct) = WriteCase(contours);
            var associations = new List<RoiAssociation>
            {
                new RoiAssociation { CanonicalName = "Lung_R", Aliases = { "Lung-R" } },
                new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "Lung-L" } },
            };
            var service = new NiftiConversionService(new RtStructMaskService());

            var written = service.ConvertStructToNifti(rtStruct, image, OutDir("out"),
                associations, true, true, null, CancellationToken.None);
            var surveyed = service.ComputeStructVolumes(rtStruct, image, associations, true, null, CancellationToken.None);

            Assert.Equal(new[] { "Cord", "Heart", "Lung_L", "Body", "Esophagus", "Lung_R" }, written.Keys);
            Assert.Equal(written.ToList(), surveyed.ToList());
        }

        /// <summary>
        /// ROIs that share a name come back from the rasterizer with their ROINumber appended to
        /// all but the lowest-numbered; each follows its namesake.
        /// </summary>
        [Fact]
        public void RoisSharingAName_FollowTheirNamesake()
        {
            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("Body", DicomTestData.SquareContour(2, 12, 1)),
                new KeyValuePair<string, double[]>("GTV", DicomTestData.SquareContour(2, 5, 1)),
                new KeyValuePair<string, double[]>("Cord", DicomTestData.SquareContour(2, 6, 1)),
                new KeyValuePair<string, double[]>("GTV", DicomTestData.SquareContour(2, 7, 1)),
                new KeyValuePair<string, double[]>("Heart", DicomTestData.SquareContour(2, 8, 1)),
                new KeyValuePair<string, double[]>("Lung", DicomTestData.SquareContour(2, 9, 1)),
            };
            var (image, rtStruct) = WriteCase(contours);
            var service = new NiftiConversionService(new RtStructMaskService());

            var written = service.ConvertStructToNifti(rtStruct, image, OutDir("out"),
                null, true, true, null, CancellationToken.None);
            var surveyed = service.ComputeStructVolumes(rtStruct, image, null, true, null, CancellationToken.None);

            Assert.Equal(new[] { "Body", "GTV", "GTV_4", "Cord", "Heart", "Lung" }, written.Keys);
            Assert.Equal(written.ToList(), surveyed.ToList());
        }

        /// <summary>
        /// The rule itself, without the native: renamed ROIs by ascending ROINumber, not ordinally,
        /// and any key the export list cannot place goes last, ordinally.
        /// </summary>
        [Fact]
        public void TheOrder_IsTotal_WhateverOrderTheVolumesArriveIn()
        {
            var dicomNames = new List<string> { "PTV", "Cord", "PTV", "PTV", "Body" };
            var export = NiftiConversionService.ResolveRoiNames(dicomNames, null, true);
            var volumes = new[] { "Zzz", "Body", "PTV_12", "Aaa", "Cord", "PTV", "PTV_7" }
                .Select((k, i) => new KeyValuePair<string, double>(k, i))
                .ToList();
            var expected = new[] { "PTV", "PTV_7", "PTV_12", "Cord", "Body", "Aaa", "Zzz" };

            Assert.Equal(expected, NiftiConversionService.InStructureSetOrder(volumes, export, dicomNames).Keys);
            volumes.Reverse();
            Assert.Equal(expected, NiftiConversionService.InStructureSetOrder(volumes, export, dicomNames).Keys);
        }

        private string OutDir(string name)
        {
            string dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private (DicomSeriesGroup image, DicomSeriesGroup rtStruct) WriteCase(
            IList<KeyValuePair<string, double[]>> contours)
        {
            string ctDir = Path.Combine(_dir, "ct");
            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, seriesUid, frameUid, studyUid: studyUid);
            string rtPath = DicomTestData.WriteRtStructWithContours(
                _dir, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), frameUid, contours);

            var image = new DicomSeriesGroup
            {
                SeriesInstanceUID = seriesUid,
                Modality = "CT",
                FrameOfReferenceUID = frameUid,
                FilePaths = Directory.GetFiles(ctDir, "*.dcm").OrderBy(p => p, StringComparer.Ordinal).ToList(),
            };
            // RoiNames as the CLI reads them: StructureSetROISequence order, duplicates kept.
            var rtStruct = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { rtPath },
            };
            foreach (var entry in contours) rtStruct.RoiNames.Add(entry.Key);
            return (image, rtStruct);
        }
    }
}
