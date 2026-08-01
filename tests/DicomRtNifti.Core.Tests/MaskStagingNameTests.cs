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
    /// Both --reverse forms copy their inputs into
    /// %TEMP%\rt_mask_validation_stage_&lt;random&gt;\masks\ before the writer sees them, so the
    /// path that has to fit MAX_PATH is the staged one, not the caller's. Measured exactly: a
    /// staged path of 259 characters worked and 260 did not — about 173 characters of basename
    /// with a default %TEMP%, however short the user's own folder was. Over that, SimpleITK failed
    /// to read the staged file, the error quoted a temporary directory the caller had never named,
    /// the ROI was absent from the RTSTRUCT and the run exited 0. It also fired before the writer's
    /// 64-character ROIName truncation, so that fix never got a chance to run.
    ///
    /// The staged name is now clipped to the ROIName cap, which the RTSTRUCT would have applied
    /// anyway — so the mask is processed and the stored name is the one a short path would have
    /// produced.
    /// </summary>
    public class MaskStagingNameTests
    {
        private static string StagedDir(string root) => Path.Combine(root, "stage", "masks");

        [Fact]
        public void AnOverLongName_IsStagedShortEnoughToOpen()
        {
            string root = DicomTestData.NewTempDir();
            string stagedMasks = StagedDir(root);
            Directory.CreateDirectory(stagedMasks);

            string longName = new string('L', 200);
            string source = Path.Combine(root, longName + ".nii.gz");

            var notices = new List<string>();
            var staged = MaskStagingNames.BuildStagedFileNames(
                stagedMasks, new[] { source }, notices);

            string stagedPath = Path.Combine(stagedMasks, staged[source]);
            Assert.True(stagedPath.Length <= MaskStagingNames.MaxStagedPathLength,
                $"staged path is {stagedPath.Length} characters");
            Assert.EndsWith(".nii.gz", staged[source]);

            // Clipped to the cap the RTSTRUCT applies anyway, so the ROI name is unchanged by this.
            Assert.Equal(new string('L', 64), NiftiFileNamingBaseName(staged[source]));

            // And it says so, naming the caller's file rather than the staging path.
            string notice = Assert.Single(notices);
            Assert.Contains(source, notice);
            Assert.DoesNotContain("rt_mask_validation_stage", notice);
        }

        /// <summary>Ordinary names are staged verbatim; nothing about the common case changes.</summary>
        [Fact]
        public void OrdinaryNames_AreStagedUnchanged_AndSaySoQuietly()
        {
            string root = DicomTestData.NewTempDir();
            string stagedMasks = StagedDir(root);

            var notices = new List<string>();
            var staged = MaskStagingNames.BuildStagedFileNames(
                stagedMasks,
                new[] { Path.Combine(root, "Lung_L.nii.gz"), Path.Combine(root, "Cord.nii") },
                notices);

            Assert.Equal("Lung_L.nii.gz", staged[Path.Combine(root, "Lung_L.nii.gz")]);
            Assert.Equal("Cord.nii", staged[Path.Combine(root, "Cord.nii")]);
            Assert.Empty(notices);
        }

        /// <summary>
        /// Two over-long names that agree on their first 64 characters must not become one staged
        /// file — that would trade a dropped ROI for an overwritten one.
        /// </summary>
        [Fact]
        public void NamesThatAgreeOnceClipped_StayDistinct()
        {
            string root = DicomTestData.NewTempDir();
            string stagedMasks = StagedDir(root);

            string shared = new string('P', 180);
            string a = Path.Combine(root, shared + "_left.nii.gz");
            string b = Path.Combine(root, shared + "_right.nii.gz");

            var staged = MaskStagingNames.BuildStagedFileNames(stagedMasks, new[] { a, b });

            Assert.NotEqual(
                staged[a].ToLowerInvariant(),
                staged[b].ToLowerInvariant());
        }

        /// <summary>
        /// End to end through the real writer: a 200-character mask basename produces an RTSTRUCT
        /// that actually contains the ROI. Before the fix the staged copy could not be opened, the
        /// ROI was dropped, and the run reported success.
        /// </summary>
        [Fact]
        public void AnOverLongMask_SurvivesIntoTheRtStruct()
        {
            string root = DicomTestData.NewTempDir();
            string ctDir = Path.Combine(root, "ct");
            string sourceMasks = Path.Combine(root, "masks_in");
            Directory.CreateDirectory(sourceMasks);

            string seriesUid = DicomTestData.NewUid();
            string studyUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(ctDir, seriesUid, frameUid, studyUid: studyUid);

            // Produce a real mask volume on that grid, then rename it to an over-long basename.
            string forwardOut = Path.Combine(root, "forward");
            Directory.CreateDirectory(forwardOut);
            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("Cord", DicomTestData.SquareContour(2, 8, 1)),
            };
            string rtstructPath = DicomTestData.WriteRtStructWithContours(
                root, "rtstruct.dcm", studyUid, DicomTestData.NewUid(), frameUid, contours);

            var image = new DicomSeriesGroup
            {
                SeriesInstanceUID = seriesUid,
                Modality = "CT",
                FrameOfReferenceUID = frameUid,
                FilePaths = Directory.GetFiles(ctDir, "*.dcm").OrderBy(p => p, StringComparer.Ordinal).ToList(),
            };
            var structSeries = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { rtstructPath },
            };
            structSeries.RoiNames.Add("Cord");

            new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                structSeries, image, forwardOut, null, true, true, null, CancellationToken.None);

            string longName = new string('R', 200);
            File.Move(Path.Combine(forwardOut, "Cord.nii.gz"),
                      Path.Combine(sourceMasks, longName + ".nii.gz"));

            // Stage exactly as the CLI does, into a staging root of realistic depth.
            string stage = Path.Combine(Path.GetTempPath(),
                "rt_mask_validation_stage_" + Path.GetRandomFileName());
            string stagedMasks = Path.Combine(stage, "masks");
            Directory.CreateDirectory(stagedMasks);
            try
            {
                var staged = MaskStagingNames.BuildStagedFileNames(
                    stagedMasks, Directory.GetFiles(sourceMasks));
                foreach (var entry in staged)
                    File.Copy(entry.Key, Path.Combine(stagedMasks, entry.Value), overwrite: true);

                string outputPath = Path.Combine(root, "out_rtstruct.dcm");
                new RtStructWriterService().ConvertMasksFolderToRtStruct(
                    dicomFolder: stage,
                    referenceSeries: image,
                    outputPath: outputPath,
                    progress: null,
                    ct: CancellationToken.None);

                var written = DicomFile.Open(outputPath).Dataset;
                var rois = written.GetSequence(DicomTag.StructureSetROISequence).Items
                    .Select(i => i.GetSingleValueOrDefault(DicomTag.ROIName, ""))
                    .ToList();

                string roi = Assert.Single(rois);
                Assert.Equal(new string('R', 64), roi);
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// A staging root so deep that nothing fits has to say so and stop, naming the caller's own
        /// file — the outcome that is not acceptable is a partial RTSTRUCT at exit 0.
        /// </summary>
        [Fact]
        public void AnImpossibleStagingRoot_FailsLoudly_NamingTheUsersFile()
        {
            string stagedMasks = @"C:\" + new string('d', 250) + @"\masks";
            string source = Path.Combine(@"C:\work", "Cord.nii.gz");

            var ex = Assert.Throws<InvalidOperationException>(
                () => MaskStagingNames.BuildStagedFileNames(stagedMasks, new[] { source }));

            Assert.Contains(source, ex.Message);
            Assert.Contains("TMP/TEMP", ex.Message);
        }

        /// <summary>Basename of a staged file name, stripping the NIfTI extension.</summary>
        private static string NiftiFileNamingBaseName(string fileName)
        {
            return fileName.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - ".nii.gz".Length)
                : Path.GetFileNameWithoutExtension(fileName);
        }
    }
}
