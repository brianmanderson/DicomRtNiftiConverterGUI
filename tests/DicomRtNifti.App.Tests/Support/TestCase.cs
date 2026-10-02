using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Tests;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// The DICOM input the forward workflow scans: a CT series with a linked RTSTRUCT in one
    /// folder, plus a fresh output root. Two sources:
    ///
    /// 1. <c>RTMASK_FIXTURE_DIR</c> set (the conformance lanes set it to the rtmask-conformance
    ///    fixture they already generated): <c>refct/*.dcm</c> and <c>rtstruct/primitives_planar.dcm</c>
    ///    are copied into one input folder, so the GUI rasterizes the same analytic primitives the
    ///    CLI gate verifies. Only the DICOM is copied: scanning the whole fixture would log every
    ///    ground-truth NIfTI as a skipped file.
    /// 2. Otherwise a 16x16x4 CT at 1 mm with a two-ROI RTSTRUCT is synthesized in memory with
    ///    fo-dicom (the same writers Core.Tests uses), so <c>dotnet test</c> works anywhere.
    ///
    /// Everything lives under one temp root that is deleted on dispose.
    /// </summary>
    public sealed class TestCase : IDisposable
    {
        public const string FixtureEnvVar = "RTMASK_FIXTURE_DIR";

        public string Root { get; }
        public string InputFolder { get; }
        public string OutputFolder { get; }
        public string Source { get; }
        public int SliceCount { get; }
        public IReadOnlyList<string> RoiNames { get; }

        public TestCase()
        {
            Root = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            InputFolder = Path.Combine(Root, "dicom");
            OutputFolder = Path.Combine(Root, "out");
            Directory.CreateDirectory(InputFolder);
            Directory.CreateDirectory(OutputFolder);

            string fixture = Environment.GetEnvironmentVariable(FixtureEnvVar);
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                string refct = Path.Combine(fixture, "refct");
                string rtstruct = Path.Combine(fixture, "rtstruct", "primitives_planar.dcm");
                if (!Directory.Exists(refct) || !File.Exists(rtstruct))
                    throw new DirectoryNotFoundException(
                        $"{FixtureEnvVar}={fixture} does not look like an rtmask-conformance fixture (refct/ + rtstruct/primitives_planar.dcm)");

                var slices = Directory.GetFiles(refct, "*.dcm");
                foreach (var f in slices)
                    File.Copy(f, Path.Combine(InputFolder, Path.GetFileName(f)));
                File.Copy(rtstruct, Path.Combine(InputFolder, "primitives_planar.dcm"));

                Source = "rtmask-conformance fixture " + fixture;
                SliceCount = slices.Length;
                RoiNames = ReadRoiNames(Path.Combine(InputFolder, "primitives_planar.dcm"));
            }
            else
            {
                string studyUid = DicomTestData.NewUid();
                string ctSeriesUid = DicomTestData.NewUid();
                string frameUid = DicomTestData.NewUid();
                SliceCount = 4;
                DicomTestData.WriteCtSeriesWithPixels(InputFolder, ctSeriesUid, frameUid,
                    sliceCount: SliceCount, size: 16, studyUid: studyUid);

                var contours = new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                    new KeyValuePair<string, double[]>("Avoid", DicomTestData.SquareContour(2, 6, 2)),
                };
                DicomTestData.WriteRtStructWithContours(InputFolder, "rtstruct.dcm",
                    studyUid, DicomTestData.NewUid(), frameUid, contours);

                Source = "synthetic fo-dicom case";
                RoiNames = contours.Select(c => c.Key).ToList();
            }
        }

        private static IReadOnlyList<string> ReadRoiNames(string rtstructPath)
        {
            var file = FellowOakDicom.DicomFile.Open(rtstructPath);
            var seq = file.Dataset.GetSequence(FellowOakDicom.DicomTag.StructureSetROISequence);
            return seq.Items.Select(i => i.GetSingleValue<string>(FellowOakDicom.DicomTag.ROIName)).ToList();
        }

        /// <summary>The one series folder the forward export wrote (image.nii.gz + masks/).</summary>
        public string FindExportedSeriesFolder()
        {
            var hits = Directory.GetFiles(OutputFolder, "image.nii.gz", SearchOption.AllDirectories);
            if (hits.Length != 1)
                throw new InvalidOperationException($"expected exactly one exported image.nii.gz under {OutputFolder}, found {hits.Length}");
            return Path.GetDirectoryName(hits[0]);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a straggling handle on Windows; the temp folder is per-run anyway */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
