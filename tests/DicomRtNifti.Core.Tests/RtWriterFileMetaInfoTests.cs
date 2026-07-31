using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Regression tests for the Group 0002 File Meta Information invariant.
    ///
    /// Both RT writers used to call <c>ds.AddOrUpdate(DicomTag.MediaStorageSOPClassUID, ...)</c>
    /// and <c>MediaStorageSOPInstanceUID</c> on the *dataset*. fo-dicom also populates the File
    /// Meta Information automatically from the <see cref="DicomFile"/> constructor, so the written
    /// file carried those two Group 0002 elements twice: once in the File Meta Information group
    /// and again as the first two elements of the dataset, past the File Meta Info boundary.
    /// That violates DICOM Part 10 (PS3.10 7.1: Group 0002 elements exist only in the File Meta
    /// Information) and Eclipse rejects the file outright.
    ///
    /// RTDOSE was fixed in 58383d9 and RTSTRUCT in 6a9e156; neither had a test, which is why the
    /// RTSTRUCT copy of the bug survived the first fix. These tests cover both writers.
    ///
    /// The seam is the shell builder rather than the public ConvertXxx entry point: the shells are
    /// where the offending tags were written, and they are pure (no masks folder, no reference
    /// image series, no SimpleITK native), so this runs anywhere the rest of the unit tests do.
    /// </summary>
    public class RtWriterFileMetaInfoTests
    {
        private const string RtStructSopClassUid = "1.2.840.10008.5.1.4.1.1.481.3";
        private const string RtDoseSopClassUid = "1.2.840.10008.5.1.4.1.1.481.2";

        [Fact]
        public void RtStructShell_WrittenFile_KeepsGroup0002OutOfDatasetBody()
        {
            AssertWriterHonorsPart10(
                BuildRtStructShellFromSyntheticReference(),
                RtStructSopClassUid,
                "rtstruct.dcm");
        }

        [Fact]
        public void RtDoseShell_WrittenFile_KeepsGroup0002OutOfDatasetBody()
        {
            AssertWriterHonorsPart10(
                BuildRtDoseShellFromSyntheticReference(),
                RtDoseSopClassUid,
                "rtdose.dcm");
        }

        /// <summary>
        /// The image-less RT-STRUCT path (reverse conversion driven by metadata.json alone, with no
        /// reference DICOM series) walks different branches of the shell builder, so it gets its own
        /// pass over the same invariant.
        /// </summary>
        [Fact]
        public void RtStructShell_WithoutReferenceSeries_KeepsGroup0002OutOfDatasetBody()
        {
            var metadata = new NiftiPatientMetadata
            {
                StudyInstanceUid = DicomTestData.NewUid(),
                ImageSeriesInstanceUid = DicomTestData.NewUid(),
                ImageModality = "CT",
                ImageSopInstanceUids = new List<string> { DicomTestData.NewUid(), DicomTestData.NewUid() },
            };

            var shell = RtStructWriterService.BuildRtStructShell(
                new DicomDataset(),
                referenceSeries: null,
                sliceLookup: new Dictionary<int, (string sopClassUid, string sopInstanceUid)>(),
                metadata: metadata);

            AssertWriterHonorsPart10(shell, RtStructSopClassUid, "rtstruct-imageless.dcm");
        }

        // ---------- Helpers ----------

        /// <summary>
        /// Saves the shell exactly the way the writers do (<c>new DicomFile(ds).Save(path)</c>),
        /// reads it back, and asserts the Part 10 split: Group 0002 lives only in the File Meta
        /// Information, and the MediaStorage UIDs there agree with the dataset's SOP Common pair.
        /// </summary>
        private static void AssertWriterHonorsPart10(
            DicomDataset shell, string expectedSopClassUid, string fileName)
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string path = Path.Combine(dir, fileName);
                new DicomFile(shell).Save(path);

                var readBack = DicomFile.Open(path);

                // The bug: these two Group 0002 elements were written into the dataset body.
                var strays = CollectGroup0002Tags(readBack.Dataset).ToList();
                Assert.True(
                    strays.Count == 0,
                    "Group 0002 (File Meta Information) elements must not appear in the dataset " +
                    "body -- DICOM PS3.10 7.1. Found: " + string.Join(", ", strays));

                // ...and the File Meta Information must still carry them, correctly.
                Assert.Equal(expectedSopClassUid, readBack.FileMetaInfo.MediaStorageSOPClassUID.UID);
                Assert.Equal(
                    readBack.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
                    readBack.FileMetaInfo.MediaStorageSOPInstanceUID.UID);

                // The fix removed only the Group 0002 duplicates; the SOP Common pair
                // (0008,0016)/(0008,0018) is Type 1 and must survive in the dataset.
                Assert.Equal(
                    expectedSopClassUid,
                    readBack.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID));
                Assert.False(
                    string.IsNullOrEmpty(readBack.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// Walks the dataset (including sequence items) and yields every Group 0002 tag found.
        /// </summary>
        private static IEnumerable<string> CollectGroup0002Tags(DicomDataset ds)
        {
            foreach (var item in ds)
            {
                if (item.Tag.Group == 0x0002)
                    yield return item.Tag.ToString();

                if (item is DicomSequence seq)
                    foreach (var nested in seq.Items)
                        foreach (var tag in CollectGroup0002Tags(nested))
                            yield return tag;
            }
        }

        private static DicomDataset BuildRtStructShellFromSyntheticReference()
        {
            var refDs = SyntheticCtSlice(out string frameUid, out _);

            var sliceLookup = new Dictionary<int, (string sopClassUid, string sopInstanceUid)>
            {
                { 0, (DicomUID.CTImageStorage.UID, DicomTestData.NewUid()) },
                { 1, (DicomUID.CTImageStorage.UID, DicomTestData.NewUid()) },
            };

            return RtStructWriterService.BuildRtStructShell(
                refDs,
                new DicomSeriesGroup
                {
                    SeriesInstanceUID = DicomTestData.NewUid(),
                    Modality = "CT",
                    FrameOfReferenceUID = frameUid,
                },
                sliceLookup,
                metadata: null);
        }

        private static DicomDataset BuildRtDoseShellFromSyntheticReference()
        {
            var refDs = SyntheticCtSlice(out string frameUid, out _);

            return RtDoseWriterService.BuildRtDoseShell(
                refDs,
                new DicomSeriesGroup
                {
                    SeriesInstanceUID = DicomTestData.NewUid(),
                    Modality = "CT",
                    FrameOfReferenceUID = frameUid,
                },
                baseName: "dose",
                timestamp: "20260731_090000");
        }

        /// <summary>
        /// A minimal CT slice dataset standing in for the reference series the writers copy
        /// patient / study identity from.
        /// </summary>
        private static DicomDataset SyntheticCtSlice(out string frameUid, out string studyUid)
        {
            frameUid = DicomTestData.NewUid();
            studyUid = DicomTestData.NewUid();

            return new DicomDataset
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomTestData.NewUid() },
                { DicomTag.PatientID, "PAT001" },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, DicomTestData.NewUid() },
                { DicomTag.Modality, "CT" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.StudyID, "1" },
            };
        }
    }
}
