using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Server-mode output names are a hash of the input mask names; re-running on the same
    /// folder must land on the same file, so these values are pinned. Changing the normalisation,
    /// the separator or the digest length renames every existing RTSTRUCT_*.dcm.
    /// </summary>
    public class HashNamingTests
    {
        [Fact]
        public void ComputeStableHash_IsDeterministic_AndOrderInsensitive()
        {
            Assert.Equal(HashNaming.ComputeStableHash(new[] { "a", "b" }), HashNaming.ComputeStableHash(new[] { "b", "a" }));
            Assert.Equal("0eab8a0a3380", HashNaming.ComputeStableHash(new[] { "a", "b" }));
        }

        [Fact]
        public void ComputeStableHash_IsCaseInsensitive()
        {
            Assert.Equal(HashNaming.ComputeStableHash(new[] { "Lung_L", "Lung_R" }), HashNaming.ComputeStableHash(new[] { "lung_l", "LUNG_R" }));
            Assert.Equal("4ae3f4a90577", HashNaming.ComputeStableHash(new[] { "Lung_L", "Lung_R" }));
            Assert.Equal(HashNaming.ComputeStableHash(new[] { "Ü" }), HashNaming.ComputeStableHash(new[] { "ü" }));
        }

        [Fact]
        public void EmptyAndNullInputs_HaveFixedValues()
        {
            Assert.Equal("e3b0c44298fc", HashNaming.ComputeStableHash(new string[0]));
            Assert.Equal("000000000000", HashNaming.ComputeStableHash(null));
            Assert.Equal(HashNaming.ComputeStableHash(new[] { "" }), HashNaming.ComputeStableHash(new string[] { null }));
        }

        [Theory]
        [InlineData("Lung_L")]
        [InlineData("a|b")]
        [InlineData("")]
        public void Output_IsTwelveLowercaseHexDigits(string name)
        {
            Assert.Matches("^[0-9a-f]{12}$", HashNaming.ComputeStableHash(new[] { name }));
        }

        [Fact]
        public void FileNames_CarryPrefixHashAndExtension()
        {
            Assert.Equal("RTSTRUCT_4ae3f4a90577.dcm", HashNaming.RtStructFileName(new[] { "Lung_L", "Lung_R" }));
            Assert.Equal("RTDOSE_daf81fe9648c.dcm", HashNaming.RtDoseFileName("dose"));
        }

        [Fact]
        public void JoinSeparatorInAName_CollidesWithTheSplitList()
        {
            // Pinned, not fixed: the names are joined with '|' before hashing, so a name that
            // contains the separator hashes like the list it splits into. Changing the separator
            // would rename every server-mode output ever written.
            Assert.Equal(HashNaming.ComputeStableHash(new[] { "a|b" }), HashNaming.ComputeStableHash(new[] { "a", "b" }));
        }
    }
}
