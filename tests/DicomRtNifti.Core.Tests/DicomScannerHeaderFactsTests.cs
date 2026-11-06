using System.Collections.Generic;
using System.Globalization;
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
    /// The header values the scanner keeps for the header-tier analysis (W4-F): orientation, grid,
    /// rescale, frames, the modality tally, every frame a structure set names, and the names a
    /// structure set repeats. Read from the dataset the scan already opens; the grouping and
    /// linking they sit beside are pinned by <see cref="DicomScannerGroupingFingerprintTests"/>.
    /// </summary>
    public class DicomScannerHeaderFactsTests
    {
        [Fact]
        public async Task ImageSeries_CarryTheirGridFacts_AndAgreeingFilesRaiseNoVariesFlag()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                for (int i = 0; i < 3; i++)
                    AnalysisCohort.Slice(root, $"ct_{i}.dcm", "CT", "P1", "1.5", "1.5.1", "1.5.90", "CT", i * 2.0, AnalysisCohort.Tilted10);

                var series = Single(await Scan(root), "1.5.1");

                Assert.Equal(AnalysisCohort.Tilted10, series.ImageOrientationPatient);
                Assert.False(series.OrientationVaries);
                Assert.Equal(16, series.Rows);
                Assert.Equal(20, series.Columns);
                Assert.Equal(1.0, series.RescaleSlope);
                Assert.Equal(-1024.0, series.RescaleIntercept);
                Assert.True(series.HasRescale);
                Assert.False(series.RescaleVaries);
                Assert.Null(series.NumberOfFrames);
                Assert.Equal(new Dictionary<string, int> { { "CT", 3 } }, series.ModalityCounts);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task FilesThatDisagree_SetTheVariesFlags_WhicheverIsWalkedFirst()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                AnalysisCohort.Slice(root, "a.dcm", "PT", "P1", "1.6", "1.6.1", "1.6.90", "PET", 0.0, AnalysisCohort.Axial, rescaleSlope: 0.5);
                AnalysisCohort.Slice(root, "b.dcm", "PT", "P1", "1.6", "1.6.1", "1.6.90", "PET", 2.0, AnalysisCohort.Axial, rescaleSlope: 0.75);
                AnalysisCohort.Slice(root, "c.dcm", "PT", "P1", "1.6", "1.6.1", "1.6.90", "PET", 4.0, AnalysisCohort.InPlaneRotated30, rescaleSlope: 0.5);

                var series = Single(await Scan(root), "1.6.1");

                Assert.True(series.OrientationVaries);
                Assert.True(series.RescaleVaries);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task TheModalityTally_IsKept_AfterTheMajorityIsChosen()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                for (int i = 0; i < 3; i++)
                    AnalysisCohort.Slice(root, $"mr_{i}.dcm", "MR", "P1", "1.7", "1.7.1", "1.7.90", "MR", i, AnalysisCohort.Axial);
                AnalysisCohort.Slice(root, "sc.dcm", "OT", "P1", "1.7", "1.7.1", "1.7.90", "MR", 3, AnalysisCohort.Axial);

                var series = Single(await Scan(root), "1.7.1");

                Assert.Equal("MR", series.Modality);
                Assert.Equal(new[] { "MR", "OT" }, series.ModalityCounts.Keys);
                Assert.Equal(new[] { 3, 1 }, series.ModalityCounts.Values);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task AMultiFrameObject_KeepsItsNumberOfFrames()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                {
                    { DicomTag.SOPClassUID, DicomUID.EnhancedCTImageStorage },
                    { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                    { DicomTag.PatientID, "P1" },
                    { DicomTag.StudyInstanceUID, "1.8" },
                    { DicomTag.SeriesInstanceUID, "1.8.1" },
                    { DicomTag.Modality, "CT" },
                    { DicomTag.FrameOfReferenceUID, "1.8.90" },
                    { DicomTag.NumberOfFrames, "40" },
                    { DicomTag.Rows, (ushort)64 },
                    { DicomTag.Columns, (ushort)64 },
                };
                new DicomFile(ds).Save(Path.Combine(root, "enhanced.dcm"));

                var series = Single(await Scan(root), "1.8.1");

                Assert.Equal(40, series.NumberOfFrames);
                Assert.Single(series.FilePaths);
                Assert.Empty(series.UnpositionedSlices);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task AStructureSet_KeepsEveryFrameItNames_AndTheNamesItRepeats()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                {
                    { DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage },
                    { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                    { DicomTag.PatientID, "P1" },
                    { DicomTag.StudyInstanceUID, "1.9" },
                    { DicomTag.SeriesInstanceUID, "1.9.3" },
                    { DicomTag.Modality, "RTSTRUCT" },
                };
                ds.Add(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence,
                    new DicomDataset { { DicomTag.FrameOfReferenceUID, "1.9.90" } },
                    new DicomDataset { { DicomTag.FrameOfReferenceUID, "1.9.91" } }));
                var rois = new[] { ("GTV", "1.9.90"), ("Lung", "1.9.91"), ("GTV", "1.9.90"), ("gtv", "1.9.92"), ("Lung", "1.9.91") }
                    .Select((r, i) => new DicomDataset
                    {
                        { DicomTag.ROINumber, (i + 1).ToString(CultureInfo.InvariantCulture) },
                        { DicomTag.ReferencedFrameOfReferenceUID, r.Item2 },
                        { DicomTag.ROIName, r.Item1 },
                    }).ToArray();
                ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, rois));
                new DicomFile(ds).Save(Path.Combine(root, "rs.dcm"));

                var rs = Single(await Scan(root), "1.9.3");

                Assert.Equal("1.9.90", rs.FrameOfReferenceUID);
                Assert.Equal(new[] { "1.9.90", "1.9.91", "1.9.92" }, rs.FrameOfReferenceUIDs);
                Assert.Equal(new[] { "GTV", "Lung", "gtv" }, rs.RoiNames);
                Assert.Equal(new[] { "GTV", "Lung" }, rs.DuplicateRoiNames);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task SeriesWithoutTheTags_LeaveTheFactsEmpty()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                DicomTestData.WriteImageSlice(root, "ct.dcm", "CT", "P1", "1.10", "1.10.1", "1.10.90", 0.0);
                DicomTestData.WriteRtDose(root, "dose.dcm", "P1", "1.10", "1.10.5", "1.10.90");

                var scan = await Scan(root);
                var ct = Single(scan, "1.10.1");
                var dose = Single(scan, "1.10.5");

                Assert.Null(ct.ImageOrientationPatient);
                Assert.Null(ct.Rows);
                Assert.False(ct.HasRescale);
                Assert.Null(ct.RescaleSlope);
                Assert.Empty(ct.FrameOfReferenceUIDs);
                Assert.Empty(dose.DuplicateRoiNames);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static Task<DicomScanResult> Scan(string root) =>
            new DicomScannerService().ScanFolderAsync(root, null, CancellationToken.None);

        private static DicomSeriesGroup Single(DicomScanResult scan, string uid) =>
            scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series).Single(s => s.SeriesInstanceUID == uid);
    }
}
