using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Guards the curated tab lists, the computed-option lists, and the friendly-name rule that
    /// keys the sectioned metadata.json.
    /// </summary>
    public class MetadataTagCatalogTests
    {
        public static IEnumerable<object[]> CuratedLists()
        {
            yield return new object[] { MetadataTagCatalog.CuratedImageKeywords };
            yield return new object[] { MetadataTagCatalog.CuratedStructureKeywords };
            yield return new object[] { MetadataTagCatalog.CuratedDoseKeywords };
        }

        [Theory]
        [MemberData(nameof(CuratedLists))]
        public void CuratedKeywords_ExistInDictionary_AndHaveNoDuplicates(IReadOnlyList<string> curated)
        {
            var valid = DicomMetadataExtractor.GetSelectableTags().Select(t => t.Keyword).ToHashSet();

            foreach (var keyword in curated)
                Assert.Contains(keyword, valid); // typo / non-existent keyword would fail here

            Assert.Equal(curated.Count, curated.Distinct().Count()); // no duplicates within a tab
        }

        [Fact]
        public void ComputedOptions_HavePrefixedKeys_AndFriendlyNames()
        {
            var all = MetadataTagCatalog.ImageComputedOptions
                .Concat(MetadataTagCatalog.StructureComputedOptions)
                .Concat(MetadataTagCatalog.DoseComputedOptions);

            foreach (var option in all)
            {
                Assert.True(MetadataTagCatalog.IsComputedKey(option.Key), $"{option.Key} should start with '@'");
                Assert.False(string.IsNullOrWhiteSpace(option.FriendlyName));
                Assert.Equal(option.FriendlyName, MetadataTagCatalog.FriendlyName(option.Key));
            }
        }

        [Theory]
        [InlineData("PatientName", "Patient Name")]
        [InlineData("PatientID", "Patient ID")]
        [InlineData("SOPInstanceUID", "SOP Instance UID")]
        [InlineData("ROIName", "ROI Name")]
        public void FriendlyName_SplitsPascalCaseAndAcronyms(string keyword, string expected)
        {
            Assert.Equal(expected, MetadataTagCatalog.FriendlyName(keyword));
        }

        [Fact]
        public void IsComputedKey_DistinguishesComputedFromRaw()
        {
            Assert.True(MetadataTagCatalog.IsComputedKey("@VoxelSize"));
            Assert.False(MetadataTagCatalog.IsComputedKey("PatientName"));
            Assert.False(MetadataTagCatalog.IsComputedKey(""));
            Assert.False(MetadataTagCatalog.IsComputedKey(null));
        }
    }
}
