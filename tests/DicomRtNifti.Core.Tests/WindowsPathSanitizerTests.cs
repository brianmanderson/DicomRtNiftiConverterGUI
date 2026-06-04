using Dicom_RT_images_Csharp.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Tests that exported folder/file segments are always valid on Windows, regardless of host OS:
    /// forbidden characters, reserved device names, and trailing dots/spaces are all neutralized.
    /// </summary>
    public class WindowsPathSanitizerTests
    {
        [Theory]
        [InlineData("a:b*c?", "a_b_c_")]
        [InlineData("a/b\\c", "a_b_c")]
        [InlineData("<>|\"", "____")]
        public void ForbiddenChars_AreReplaced(string input, string expected)
        {
            Assert.Equal(expected, WindowsPathSanitizer.SanitizeName(input));
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("prn")]
        [InlineData("NUL.nii.gz")]
        [InlineData("COM1")]
        [InlineData("LPT9")]
        public void ReservedDeviceNames_ArePrefixed(string input)
        {
            string result = WindowsPathSanitizer.SanitizeName(input);
            Assert.StartsWith("_", result);
        }

        [Fact]
        public void TrailingDotsAndSpaces_AreTrimmed()
        {
            Assert.Equal("name", WindowsPathSanitizer.SanitizeName("name.  "));
            Assert.Equal("folder", WindowsPathSanitizer.SanitizeName("folder. . "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("...")]
        public void EmptyOrAllTrimmed_FallsBack(string input)
        {
            Assert.Equal("_", WindowsPathSanitizer.SanitizeName(input));
        }

        [Fact]
        public void OrdinaryName_PassesThrough()
        {
            Assert.Equal("CT_Head_20240101", WindowsPathSanitizer.SanitizeName("CT_Head_20240101"));
        }
    }
}
