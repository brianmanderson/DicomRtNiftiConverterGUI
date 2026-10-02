using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The NIfTI file-name rules the reverse direction discovers its inputs with.</summary>
    public class NiftiFileNamingTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Theory]
        [InlineData("a.nii.gz", "a")]
        [InlineData("a.NII.GZ", "a")]
        [InlineData("a.nii", "a")]
        [InlineData("a.b.nii.gz", "a.b")]
        [InlineData("a.tar.gz", "a.tar")]
        [InlineData("a", "a")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void StripNiftiExtension_HandlesTheKnownShapes(string input, string expected)
        {
            Assert.Equal(expected, NiftiFileNaming.StripNiftiExtension(input));
        }

        [Fact]
        public void StripNiftiExtension_AcceptsAFullPath()
        {
            Assert.Equal("Lung_L", NiftiFileNaming.StripNiftiExtension(Path.Combine(_dir, "masks", "Lung_L.nii.gz")));
        }

        [Fact]
        public void EnumerateNiftiFiles_FiltersDedupesAndSorts()
        {
            foreach (var name in new[] { "foo.nii.gz", "foo.nii", "bar.nii", "baz.nii.bak", "qux.niix", "readme.txt" })
                File.WriteAllText(Path.Combine(_dir, name), "");

            var warnings = new List<string>();
            var files = NiftiFileNaming.EnumerateNiftiFiles(_dir, warnings).Select(Path.GetFileName).ToList();

            Assert.Equal(new[] { "bar.nii", "foo.nii.gz" }, files);
            Assert.Equal(new[] { Path.Combine(_dir, "foo.nii") }, warnings);
        }

        [Fact]
        public void EnumerateNiftiFiles_MissingOrNullFolder_IsEmpty()
        {
            Assert.Empty(NiftiFileNaming.EnumerateNiftiFiles(Path.Combine(_dir, "nope")));
            Assert.Empty(NiftiFileNaming.EnumerateNiftiFiles(null));
        }

        [Fact]
        public void TryGetImageNiftiPath_PrefersGzip()
        {
            Assert.False(NiftiFileNaming.TryGetImageNiftiPath(_dir, out string none));
            Assert.Null(none);

            File.WriteAllText(Path.Combine(_dir, "image.nii"), "");
            Assert.True(NiftiFileNaming.TryGetImageNiftiPath(_dir, out string plain));
            Assert.Equal(Path.Combine(_dir, "image.nii"), plain);

            File.WriteAllText(Path.Combine(_dir, "image.nii.gz"), "");
            Assert.True(NiftiFileNaming.TryGetImageNiftiPath(_dir, out string gz));
            Assert.Equal(Path.Combine(_dir, "image.nii.gz"), gz);

            Assert.False(NiftiFileNaming.TryGetImageNiftiPath(null, out _));
        }
    }
}
