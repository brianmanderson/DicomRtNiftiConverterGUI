using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Metadata keyword validation moved from the CLI into Core so every front-end refuses the
    /// same lists with the same words. The CLI prefixes the flag the list came from.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class MetadataKeywordValidatorTests
    {
        [Fact]
        public void AcceptsTagsAndTheSectionsComputedValues_InTheOrderGiven()
        {
            Assert.True(MetadataKeywordValidator.TryValidate(
                " PatientAge, @VoxelSize ,,KVP", MetadataTagCatalog.ImageComputedOptions,
                out var keywords, out string error));

            Assert.Null(error);
            Assert.Equal(new[] { "PatientAge", "@VoxelSize", "KVP" }, keywords);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ")]
        public void AnEmptyList_IsAccepted(string text)
        {
            Assert.True(MetadataKeywordValidator.TryValidate(
                text, MetadataTagCatalog.DoseComputedOptions, out var keywords, out string error));
            Assert.Empty(keywords);
            Assert.Null(error);
        }

        [Fact]
        public void AnotherSectionsComputedValue_AndAMisspelledTag_AreBothNamed()
        {
            Assert.False(MetadataKeywordValidator.TryValidate(
                "PatientAge,@MaxDose,PatientAgee", MetadataTagCatalog.ImageComputedOptions,
                out _, out string error));

            string computed = string.Join(", ", MetadataTagCatalog.ImageComputedOptions.Select(o => o.Key));
            Assert.Equal(
                "unrecognised keyword(s) @MaxDose, PatientAgee. Expected fo-dicom tag keywords " +
                $"(PatientAge, KVP, DoseUnits) or one of this section's computed values ({computed}).",
                error);
        }

        /// <summary>
        /// The CLI's refusal: exit 2, the flag named, and the keywords checked before the input
        /// folder is even looked at, as they were when the check lived in the CLI.
        /// </summary>
        [Fact]
        public void Cli_RefusesAnUnknownKeyword_WithExit2_BeforeLookingAtTheInput()
        {
            string missing = Path.Combine(Path.GetTempPath(), "drt_no_such_input_" + Path.GetRandomFileName());

            var run = CliRun.Execute("--cohort-convert", "--input", missing, "--output", missing + "_out",
                "--metadata-dose-tags", "DoseUnits,@VoxelSize");

            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Equal("", run.Stdout);
            string computed = string.Join(", ", MetadataTagCatalog.DoseComputedOptions.Select(o => o.Key));
            Assert.Contains(
                "ERROR: --metadata-dose-tags: unrecognised keyword(s) @VoxelSize. Expected fo-dicom tag " +
                $"keywords (PatientAge, KVP, DoseUnits) or one of this section's computed values ({computed}).",
                run.Stderr);
            Assert.False(Directory.Exists(missing + "_out"));
        }
    }

    /// <summary>The native probe moved into Core; --version still prints its line verbatim.</summary>
    [Collection(CliConsoleCollection.Name)]
    public class SimpleItkNativeTests
    {
        [Fact]
        public void Probe_ReportsTheNativeLoaded()
        {
            Assert.True(SimpleItkNative.TryProbe(out string status), status);
            Assert.Equal("SimpleITK native: OK (1-voxel probe)", status);
            Assert.Equal(status, SimpleItkNative.Probe());
        }

        [Fact]
        public void Version_PrintsTheVersionLine_ThenTheProbeLine()
        {
            var run = CliRun.Execute("--version");

            Assert.True(run.ExitCode == 0, run.ToString());
            var lines = run.Stdout.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("DicomRtNifti CLI 1.0.0", lines[0]);
            Assert.Equal(SimpleItkNative.Probe(), lines[1]);
        }
    }
}
