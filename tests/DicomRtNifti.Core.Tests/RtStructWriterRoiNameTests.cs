using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// ROIName (3006,0026) is VR = LO, capped at 64 characters, and fo-dicom enforces that when
    /// the element is constructed. The writer put the mask file's basename straight in. A single
    /// 65-character mask file therefore threw partway through assembling the sequences — before
    /// anything was written — so the entire reverse run failed and every other ROI in the folder
    /// was discarded along with it.
    ///
    /// The sibling ROIObservationLabel two lines below had always truncated to its own 16-character
    /// limit for exactly this reason; these pin ROIName onto the same behaviour, and pin the part
    /// that actually mattered: the other ROIs survive.
    /// </summary>
    public class RtStructWriterRoiNameTests
    {
        // 70 characters. Leading "A" so it sorts before the short ROI and is written first —
        // which is where the old code threw.
        private static readonly string LongRoiName = "A" + new string('x', 69);
        private const string ShortRoiName = "Zshort";

        [Fact]
        public void OverLongRoiName_IsTruncated_AndTheOtherRoisSurvive()
        {
            string root = DicomTestData.NewTempDir();
            string dicomFolder = Path.Combine(root, "dicom");
            string outputPath = Path.Combine(root, "rtstruct_out.dcm");

            var referenceSeries = BuildMasksAndReferenceSeries(dicomFolder);

            string written = new RtStructWriterService().ConvertMasksFolderToRtStruct(
                dicomFolder, referenceSeries, outputPath, progress: null, ct: CancellationToken.None);

            var ds = DicomFile.Open(written).Dataset;
            var names = ds.GetSequence(DicomTag.StructureSetROISequence).Items
                          .Select(i => i.GetSingleValue<string>(DicomTag.ROIName))
                          .ToList();

            // The long ROI is present, clipped to the VR's limit...
            Assert.Contains(LongRoiName.Substring(0, RtStructWriterService.RoiNameMaxLength), names);
            Assert.All(names, n => Assert.True(n.Length <= RtStructWriterService.RoiNameMaxLength));

            // ...and, the point of the fix, it did not take the rest of the run down with it.
            Assert.Contains(ShortRoiName, names);
            Assert.Equal(2, names.Count);
        }

        [Fact]
        public void TruncateToVrLimit_LeavesShortValuesUntouched()
        {
            string value = "Lung_L";
            Assert.Same(value, RtStructWriterService.TruncateToVrLimit(value, RtStructWriterService.RoiNameMaxLength));
            Assert.Null(RtStructWriterService.TruncateToVrLimit(null, RtStructWriterService.RoiNameMaxLength));
        }

        [Fact]
        public void TruncateToVrLimit_ClipsToTheLimit()
        {
            string clipped = RtStructWriterService.TruncateToVrLimit(
                LongRoiName, RtStructWriterService.RoiNameMaxLength);

            Assert.Equal(RtStructWriterService.RoiNameMaxLength, clipped.Length);
            Assert.Equal(LongRoiName.Substring(0, RtStructWriterService.RoiNameMaxLength), clipped);
        }

        // ---------- helpers ----------

        /// <summary>
        /// Lays out what the writer expects: a folder of reference DICOM slices with a masks/
        /// subfolder beside them.
        ///
        /// The masks come out of the forward converter so they carry real geometry, then one is
        /// renamed to the over-long name. The rename is not a shortcut — a >64-character ROI name
        /// cannot come from an RTSTRUCT, since ROIName's VR caps it there too. It arises exactly
        /// this way: the reverse path takes whatever the user named the NIfTI files, and a
        /// filesystem happily holds 255.
        /// </summary>
        private static DicomSeriesGroup BuildMasksAndReferenceSeries(string dicomFolder)
        {
            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();

            DicomTestData.WriteCtSeriesWithPixels(
                dicomFolder, seriesUid, frameUid, sliceCount: 4, size: 16, studyUid: studyUid);

            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("placeholder", DicomTestData.SquareContour(2, 8, 1.0)),
                new KeyValuePair<string, double[]>(ShortRoiName, DicomTestData.SquareContour(4, 10, 2.0)),
            };
            string rtstructPath = DicomTestData.WriteRtStructWithContours(
                Path.GetDirectoryName(dicomFolder), "source_rtstruct.dcm",
                studyUid, DicomTestData.NewUid(), frameUid, contours);

            var referenceSeries = new DicomSeriesGroup
            {
                SeriesInstanceUID = seriesUid,
                Modality = "CT",
                FrameOfReferenceUID = frameUid,
                FilePaths = Directory.GetFiles(dicomFolder, "*.dcm")
                                     .OrderBy(p => p, StringComparer.Ordinal)
                                     .ToList(),
            };
            var structSeries = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { rtstructPath },
            };
            foreach (var c in contours) structSeries.RoiNames.Add(c.Key);

            // flatOutput: false puts the masks in <dicomFolder>/masks/, which is where
            // ConvertMasksFolderToRtStruct looks for them.
            new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                structSeries, referenceSeries, dicomFolder,
                associations: null, exportUnmatched: true, flatOutput: false,
                progress: null, ct: CancellationToken.None);

            string masksDir = Path.Combine(dicomFolder, "masks");
            File.Move(
                Path.Combine(masksDir, "placeholder.nii.gz"),
                Path.Combine(masksDir, LongRoiName + ".nii.gz"));

            return referenceSeries;
        }
    }
}
