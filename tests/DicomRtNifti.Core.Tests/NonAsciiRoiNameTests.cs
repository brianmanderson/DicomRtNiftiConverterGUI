using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Non-ASCII ROI names through the forward and reverse paths (W1-C step 8). "ü" is in every
    /// Western code page; "Ĺ" is Latin-2 only, so it probes the UTF-8 handling of each hop:
    /// fo-dicom decoding under ISO_IR 192, the mask file name on each OS, SimpleITK's path
    /// marshalling, and the RTSTRUCT the reverse writer declares.
    /// </summary>
    public class NonAsciiRoiNameTests
    {
        private const string Umlaut = "Lunge_rechts_ü";
        private const string Latin2 = "Ĺ_ROI";

        private sealed class Case : IDisposable
        {
            public string Root = DicomTestData.NewTempDir();
            public string DicomFolder;
            public string RtStructPath;
            public DicomSeriesGroup Image;
            public DicomSeriesGroup RtStruct;

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
            }
        }

        private static Case BuildCase(string charset = "ISO_IR 192")
        {
            var c = new Case { DicomFolder = Path.Combine(DicomTestData.NewTempDir(), "dicom") };
            c.DicomFolder = Path.Combine(c.Root, "dicom");
            string studyUid = DicomTestData.NewUid();
            string ctUid = DicomTestData.NewUid();
            string frameUid = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(c.DicomFolder, ctUid, frameUid, sliceCount: 4, size: 16, studyUid: studyUid);

            var contours = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>(Umlaut, DicomTestData.SquareContour(2, 8, 1.0)),
                new KeyValuePair<string, double[]>(Latin2, DicomTestData.SquareContour(4, 10, 2.0)),
            };
            c.RtStructPath = DicomTestData.WriteRtStructWithContours(c.Root, "rs.dcm", studyUid, DicomTestData.NewUid(),
                frameUid, contours, referencedSeriesUid: ctUid, specificCharacterSet: charset);

            c.Image = Cli.HeadlessRunner.BuildImageSeriesFromFolder(c.DicomFolder);
            c.RtStruct = new DicomSeriesGroup
            {
                SeriesInstanceUID = DicomTestData.NewUid(),
                Modality = "RTSTRUCT",
                FrameOfReferenceUID = frameUid,
                FilePaths = new List<string> { c.RtStructPath },
                RoiNames = contours.Select(k => k.Key).ToList(),
            };
            return c;
        }

        private static List<string> RoiNamesIn(string rtstructPath)
        {
            return DicomFile.Open(rtstructPath).Dataset
                .GetSequence(DicomTag.StructureSetROISequence).Items
                .Select(i => i.GetSingleValue<string>(DicomTag.ROIName))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        [Fact]
        public void Fixture_EncodesTheNamesItClaims()
        {
            using (var c = BuildCase())
            {
                Assert.Equal(new[] { Umlaut, Latin2 }.OrderBy(n => n, StringComparer.Ordinal), RoiNamesIn(c.RtStructPath));
            }
        }

        [Fact]
        public void Forward_ThenReverse_PreservesTheNamesExactly()
        {
            using (var c = BuildCase())
            {
                // Forward: masks/<name>.nii.gz under the DICOM folder, which is where the reverse
                // writer looks for them.
                var volumes = new NiftiConversionService(new RtStructMaskService()).ConvertStructToNifti(
                    c.RtStruct, c.Image, c.DicomFolder, associations: null, exportUnmatched: true,
                    flatOutput: false, progress: null, ct: CancellationToken.None);

                Assert.Equal(new[] { Umlaut, Latin2 }.OrderBy(n => n, StringComparer.Ordinal),
                             volumes.Keys.OrderBy(n => n, StringComparer.Ordinal));
                var maskFiles = Directory.GetFiles(Path.Combine(c.DicomFolder, "masks"), "*.nii.gz")
                    .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Assert.Equal(new[] { Umlaut + ".nii.gz", Latin2 + ".nii.gz" }.OrderBy(n => n, StringComparer.Ordinal), maskFiles);
                foreach (var f in Directory.GetFiles(Path.Combine(c.DicomFolder, "masks"), "*.nii.gz"))
                {
                    // The toolkit's own reader: a raw SimpleITK.ReadImage would fail on the
                    // Latin-2 name on Windows for the reason the helper exists.
                    using (var mask = NiftiConversionService.ReadImageSafely(f))
                    {
                        var stats = new StatisticsImageFilter();
                        stats.Execute(mask);
                        Assert.True(stats.GetSum() > 0, f);
                    }
                }

                // Reverse: the mask file names become ROI names in a UTF-8 structure set.
                string outPath = Path.Combine(c.Root, "rtstruct_out.dcm");
                string written = new RtStructWriterService().ConvertMasksFolderToRtStruct(
                    c.DicomFolder, c.Image, outPath, progress: null, ct: CancellationToken.None);

                var ds = DicomFile.Open(written).Dataset;
                Assert.Equal("ISO_IR 192", ds.GetSingleValue<string>(DicomTag.SpecificCharacterSet));
                Assert.Equal(RoiNamesIn(c.RtStructPath), RoiNamesIn(written));

                // Bytes, not just fo-dicom's in-memory string: the UTF-8 encoding of the name is
                // in the file, so the declared character set is the one the names were written in.
                byte[] bytes = File.ReadAllBytes(written);
                Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(Umlaut)) >= 0, "UTF-8 bytes of the umlaut name not found in the RTSTRUCT");
                Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(Latin2)) >= 0, "UTF-8 bytes of the Latin-2 name not found in the RTSTRUCT");
            }
        }

        [Fact]
        public void Latin2Rtstruct_RoiName_DecodesWithoutHostRegistration()
        {
            // The CLI registers CodePagesEncodingProvider in Main; the App does not; this test
            // process registers nothing. If fo-dicom did not self-register the provider, a
            // structure set declared in ISO_IR 101 would decode "Ĺ" as a replacement character here.
            using (var c = BuildCase("ISO_IR 101"))
            {
                Assert.Contains(Latin2, RoiNamesIn(c.RtStructPath));
                var latin2 = Encoding.GetEncoding(28592);
                Assert.NotNull(latin2);
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
