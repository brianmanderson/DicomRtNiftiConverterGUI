using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The scanner stops reading a file at ROIContourSequence and at the functional group
    /// sequences, which hold most of a structure set's or a segmentation's bytes. Each test here
    /// places a tag the scanner needs at or after one of those stop points, so it fails if the
    /// stop ever cuts that tag off.
    /// </summary>
    public class DicomScannerPartialReadTests
    {
        /// <summary>
        /// An RT Dose may carry the Structure Set and ROI Contour modules itself; its
        /// ReferencedStructureSetSequence (300C,0060) then follows its own ROIContourSequence.
        /// </summary>
        [Fact]
        public async Task A_dose_with_its_own_contours_still_yields_its_referenced_structure_set()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                string doseSeries = DicomTestData.NewUid(), structSop = DicomTestData.NewUid();
                var ds = Base("RTDOSE", DicomUID.RTDoseStorage, study, doseSeries, frame);
                ds.Add(new DicomSequence(DicomTag.ROIContourSequence, new DicomDataset { { DicomTag.ReferencedROINumber, "1" } }));
                ds.Add(new DicomSequence(DicomTag.ReferencedStructureSetSequence, new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.RTStructureSetStorage },
                    { DicomTag.ReferencedSOPInstanceUID, structSop },
                }));
                new DicomFile(ds).Save(Path.Combine(dir, "dose.dcm"));

                var dose = Single(await Scan(dir), doseSeries);

                Assert.Equal("RTDOSE", dose.Modality);
                Assert.Equal(structSop, dose.ReferencedSeriesUID);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// A single-frame enhanced CT has no top-level ImagePositionPatient; only
        /// PerFrameFunctionalGroupsSequence (5200,9230), which sorts after
        /// SharedFunctionalGroupsSequence, says it is a multi-frame object to keep whole rather
        /// than a slice without a position.
        /// </summary>
        [Fact]
        public async Task An_enhanced_image_is_kept_whole_not_reported_as_unpositioned()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                var ds = Base("CT", DicomUID.EnhancedCTImageStorage, study, series, frame);
                ds.Add(DicomTag.NumberOfFrames, "1");
                AddFunctionalGroups(ds, 1);
                string path = Path.Combine(dir, "enhanced.dcm");
                new DicomFile(ds).Save(path);

                var ct = Single(await Scan(dir), series);

                Assert.Equal(new[] { path }, ct.FilePaths.ToArray());
                Assert.Empty(ct.UnpositionedSlices);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task A_segmentation_is_listed_with_its_series_attributes()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                var ds = Base("SEG", DicomUID.SegmentationStorage, study, series, frame);
                ds.Add(DicomTag.NumberOfFrames, "3");
                AddFunctionalGroups(ds, 3);
                string path = Path.Combine(dir, "seg.dcm");
                new DicomFile(ds).Save(path);

                var seg = Single(await Scan(dir), series);

                Assert.Equal("SEG", seg.Modality);
                Assert.Equal(frame, seg.FrameOfReferenceUID);
                Assert.Equal("SEG series", seg.SeriesDescription);
                Assert.Equal(new[] { path }, seg.FilePaths.ToArray());
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// Everything a structure set contributes sits before its contours: ROI names, the
        /// referenced series, and the frame of reference from ReferencedFrameOfReferenceSequence
        /// and each ROI (the top-level tag is absent here, as on most real structure sets).
        /// </summary>
        [Fact]
        public async Task A_structure_set_read_up_to_its_contours_links_and_names_its_rois()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), ctSeries = DicomTestData.NewUid();
                string rtSeries = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"ct_{i}.dcm", "CT", "P1", study, ctSeries, frame, i * 3.0);
                string rtPath = DicomTestData.WriteRtStructWithContours(dir, "rt.dcm", study, rtSeries, frame,
                    new[]
                    {
                        new KeyValuePair<string, double[]>("Heart", DicomTestData.SquareContour(-5, 5, 3.0)),
                        new KeyValuePair<string, double[]>("Esophagus", DicomTestData.SquareContour(-2, 2, 6.0)),
                    },
                    patientId: "P1", referencedSeriesUid: ctSeries);
                var file = DicomFile.Open(rtPath, FileReadOption.ReadAll);
                file.Dataset.Remove(DicomTag.FrameOfReferenceUID);
                file.Dataset.Add(new DicomSequence(DicomTag.RTROIObservationsSequence, new DicomDataset { { DicomTag.ObservationNumber, "1" } }));
                file.Save(rtPath);

                var result = await Scan(dir);
                var rt = Single(result, rtSeries);
                var ct = Single(result, ctSeries);

                Assert.Equal(new[] { "Heart", "Esophagus" }, rt.RoiNames.ToArray());
                Assert.Equal(ctSeries, rt.ReferencedSeriesUID);
                Assert.Equal(frame, rt.FrameOfReferenceUID);
                Assert.Equal(RtLinkMatchRule.ReferencedSeriesUid, rt.LinkMatchRule);
                Assert.Same(rt, Assert.Single(ct.LinkedRtStructs));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// The one thing the early stop changes. A structure set whose ROIContourSequence is
        /// malformed used to fail fo-dicom's full read and drop out of the scan without an error;
        /// read up to its contours it is now listed with its ROI names, and the damage is reported
        /// when a conversion reads the contours.
        /// </summary>
        [Fact]
        public async Task A_structure_set_with_malformed_contours_is_listed_rather_than_dropped()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), rtSeries = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                string path = DicomTestData.WriteRtStruct(dir, "rt.dcm", "P1", study, rtSeries, frame, "SET", new[] { "GTV" });

                // A top-level ROIContourSequence of undefined length whose first element is not an
                // item: fo-dicom's full read throws on it.
                var tail = new List<byte>();
                tail.AddRange(new byte[] { 0x06, 0x30, 0x39, 0x00 });
                tail.AddRange(Encoding.ASCII.GetBytes("SQ"));
                tail.AddRange(new byte[] { 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
                tail.AddRange(new byte[] { 0x08, 0x00, 0x16, 0x00 });
                tail.AddRange(Encoding.ASCII.GetBytes("UI"));
                tail.AddRange(new byte[] { 4, 0 });
                tail.AddRange(Encoding.ASCII.GetBytes("1.2\0"));
                using (var stream = new FileStream(path, FileMode.Append))
                    stream.Write(tail.ToArray(), 0, tail.Count);
                Assert.ThrowsAny<DicomException>(() => DicomFile.Open(path, FileReadOption.SkipLargeTags));

                var result = await Scan(dir);
                var rt = Single(result, rtSeries);

                Assert.Equal(new[] { "GTV" }, rt.RoiNames.ToArray());
                Assert.Equal(0, result.SkippedErrorCount);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        private static Task<DicomScanResult> Scan(string dir) =>
            new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);

        private static DicomSeriesGroup Single(DicomScanResult result, string seriesUid) =>
            Assert.Single(result.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series),
                s => s.SeriesInstanceUID == seriesUid);

        private static DicomDataset Base(string modality, DicomUID sopClass, string study, string series, string frame) =>
            new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, sopClass },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, "P1" },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, study },
                { DicomTag.SeriesInstanceUID, series },
                { DicomTag.Modality, modality },
                { DicomTag.FrameOfReferenceUID, frame },
                { DicomTag.SeriesDescription, modality + " series" },
            };

        private static void AddFunctionalGroups(DicomDataset ds, int frames)
        {
            var pixelMeasures = new DicomDataset();
            pixelMeasures.Add(DicomTag.PixelSpacing, "1", "1");
            var shared = new DicomDataset();
            shared.Add(new DicomSequence(DicomTag.PixelMeasuresSequence, pixelMeasures));
            ds.Add(new DicomSequence(DicomTag.SharedFunctionalGroupsSequence, shared));

            var perFrame = new DicomDataset[frames];
            for (int i = 0; i < frames; i++)
            {
                var position = new DicomDataset();
                position.Add(DicomTag.ImagePositionPatient, "0", "0", (i * 2).ToString());
                perFrame[i] = new DicomDataset();
                perFrame[i].Add(new DicomSequence(DicomTag.PlanePositionSequence, position));
            }
            ds.Add(new DicomSequence(DicomTag.PerFrameFunctionalGroupsSequence, perFrame));
        }
    }
}
