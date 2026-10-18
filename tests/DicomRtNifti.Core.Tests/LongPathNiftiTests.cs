using System;
using System.IO;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A NIfTI written and read back at a path longer than Windows' 260-character MAX_PATH
    /// (S#107). SimpleITK's native I/O cannot open such a path on Windows, so the service's
    /// helpers detour it through a short temporary file; on Linux and macOS it goes straight
    /// through. Either way the image must come back unchanged.
    /// </summary>
    public class LongPathNiftiTests
    {
        private static Image MakeVolume()
        {
            var img = new Image(6, 5, 4, PixelIDValueEnum.sitkInt16);
            img.SetSpacing(new VectorDouble(new[] { 0.9, 1.1, 2.5 }));
            img.SetOrigin(new VectorDouble(new[] { -12.5, 7.25, 30.0 }));
            for (uint z = 0; z < 4; z++)
                for (uint y = 0; y < 5; y++)
                    for (uint x = 0; x < 6; x++)
                        img.SetPixelAsInt16(new VectorUInt32(new[] { x, y, z }), (short)(x + 10 * y + 100 * z));
            return img;
        }

        [Fact]
        public void Nifti_AtPathPastMaxPath_RoundTrips()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                // Nested folders, each name far under the 255-byte component limit of every OS.
                string dir = root;
                for (int i = 0; dir.Length < 280; i++)
                    dir = Path.Combine(dir, $"level{i}_" + new string('d', 50));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "Long_ROI_name.nii.gz");
                Assert.True(path.Length > 260, $"path is only {path.Length} characters");
                Assert.Equal(OperatingSystem.IsWindows(), NiftiConversionService.NeedsDetour(path));

                using (var original = MakeVolume())
                {
                    NiftiConversionService.WriteImageSafely(original, path);
                    Assert.Equal(new[] { path }, Directory.GetFiles(dir));

                    using (var read = NiftiConversionService.ReadImageSafely(path))
                    {
                        Assert.Equal(original.GetSize(), read.GetSize());
                        Assert.Equal(PixelIDValueEnum.sitkInt16, read.GetPixelID());
                        for (int d = 0; d < 3; d++)
                        {
                            Assert.Equal(original.GetSpacing()[d], read.GetSpacing()[d], 5);
                            Assert.Equal(original.GetOrigin()[d], read.GetOrigin()[d], 5);
                        }
                        for (uint z = 0; z < 4; z++)
                            for (uint y = 0; y < 5; y++)
                                for (uint x = 0; x < 6; x++)
                                {
                                    var idx = new VectorUInt32(new[] { x, y, z });
                                    Assert.Equal(original.GetPixelAsInt16(idx), read.GetPixelAsInt16(idx));
                                }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }
}
