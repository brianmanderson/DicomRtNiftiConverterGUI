using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Exercises the real NiftiConversionService.ResolveRoiNames mapping (DICOM ROI name -> output
    /// name) against representative associations, to confirm aliases and canonical names resolve.
    /// ResolveRoiNames is private and stateless, so we invoke it via reflection on an
    /// uninitialized service instance (no constructor dependencies are touched).
    /// </summary>
    public class RoiAssociationResolutionTests
    {
        private static Dictionary<string, string> Resolve(
            List<string> dicomRoiNames, List<RoiAssociation> associations, bool exportUnmatched)
        {
            var svc = (NiftiConversionService)RuntimeHelpers.GetUninitializedObject(typeof(NiftiConversionService));
            var method = typeof(NiftiConversionService).GetMethod(
                "ResolveRoiNames", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (Dictionary<string, string>)method.Invoke(svc, new object[] { dicomRoiNames, associations, exportUnmatched });
        }

        private static List<RoiAssociation> SampleAssociations() => new List<RoiAssociation>
        {
            new RoiAssociation { CanonicalName = "gtv", Aliases = new List<string> { "GTV-1", "gtv-2" } },
            new RoiAssociation { CanonicalName = "LungR", Aliases = new List<string> { "Lung-Right" } },
            new RoiAssociation { CanonicalName = "Lung_Left", Aliases = new List<string> { "Lung-Left" } },
            new RoiAssociation { CanonicalName = "Cord", Aliases = new List<string> { "Spinal-Cord" } },
        };

        [Fact]
        public void AliasMatch_RenamesToCanonical()
        {
            var result = Resolve(new List<string> { "Lung-Right" }, SampleAssociations(), exportUnmatched: false);
            Assert.True(result.ContainsKey("LungR"), "Expected output key 'LungR' for DICOM 'Lung-Right'");
            Assert.Equal("Lung-Right", result["LungR"]);
        }

        [Fact]
        public void CanonicalNameMatch_CaseInsensitive_RenamesToCanonical()
        {
            // DICOM "GTV" should match canonical "gtv" even though it is not in the alias list.
            var result = Resolve(new List<string> { "GTV" }, SampleAssociations(), exportUnmatched: false);
            Assert.True(result.ContainsKey("gtv"), "Expected output key 'gtv' for DICOM 'GTV'");
            Assert.Equal("GTV", result["gtv"]);
        }

        [Fact]
        public void Unmatched_DroppedWhenExportUnmatchedFalse()
        {
            var result = Resolve(new List<string> { "Heart", "Lung-Right" }, SampleAssociations(), exportUnmatched: false);
            Assert.True(result.ContainsKey("LungR"));
            Assert.False(result.ContainsKey("Heart"), "Unmatched 'Heart' should be dropped when exportUnmatched is false");
        }

        [Fact]
        public void Unmatched_KeptWhenExportUnmatchedTrue()
        {
            var result = Resolve(new List<string> { "Heart", "Lung-Right" }, SampleAssociations(), exportUnmatched: true);
            Assert.Equal("LungR", FindKeyFor(result, "Lung-Right"));
            Assert.True(result.ContainsKey("Heart"), "Unmatched 'Heart' should be kept (original name) when exportUnmatched is true");
            Assert.Equal("Heart", result["Heart"]);
        }

        [Theory]
        // Forgiving matching: '-', '_', and whitespace are interchangeable separators, case ignored.
        [InlineData("Spinal_Cord", "Cord")]     // alias "Spinal-Cord": underscore vs hyphen
        [InlineData("spinal cord", "Cord")]     // space vs hyphen
        [InlineData("Spinal  Cord", "Cord")]    // collapsed run of separators
        [InlineData("gtv_1", "gtv")]            // alias "GTV-1": underscore vs hyphen + case
        [InlineData("Lung Right", "LungR")]     // alias "Lung-Right": space vs hyphen
        public void ForgivingMatch_BridgesSeparatorVariants(string dicomName, string expectedCanonical)
        {
            var result = Resolve(new List<string> { dicomName }, SampleAssociations(), exportUnmatched: false);
            Assert.True(result.ContainsKey(expectedCanonical),
                $"Expected '{dicomName}' to resolve to canonical '{expectedCanonical}'");
            Assert.Equal(dicomName, result[expectedCanonical]);
        }

        [Theory]
        // Dial-back: a run-together name must NOT match a separated alias (no separator deletion),
        // so e.g. "SpinalCord" is left alone rather than collapsed onto canonical "Cord".
        [InlineData("SpinalCord")]   // vs alias "Spinal-Cord"
        [InlineData("GTV1")]         // vs alias "GTV-1"
        [InlineData("LungRight")]    // vs alias "Lung-Right"
        public void ForgivingMatch_DoesNotDeleteSeparators(string dicomName)
        {
            var result = Resolve(new List<string> { dicomName }, SampleAssociations(), exportUnmatched: false);
            Assert.Empty(result); // no match -> dropped when exportUnmatched is false
        }

        [Fact]
        public void ForgivingMatch_DoesNotBridgeDifferentTokens()
        {
            // "Lung_L" must NOT match alias "Lung-Left" (different tokens: 'L' vs 'Left').
            var result = Resolve(new List<string> { "Lung_L" }, SampleAssociations(), exportUnmatched: false);
            Assert.False(result.ContainsKey("Lung_Left"), "'Lung_L' should not resolve to 'Lung_Left'");
            Assert.Empty(result); // unmatched and exportUnmatched=false -> dropped
        }

        [Fact]
        public void RealWorldMix_MapsAllAliasesToCanonicals()
        {
            var dicom = new List<string> { "GTV-1", "Lung-Right", "Lung-Left", "Spinal-Cord", "Heart" };
            var result = Resolve(dicom, SampleAssociations(), exportUnmatched: true);

            Assert.Equal("GTV-1", result["gtv"]);
            Assert.Equal("Lung-Right", result["LungR"]);
            Assert.Equal("Lung-Left", result["Lung_Left"]);
            Assert.Equal("Spinal-Cord", result["Cord"]);
            Assert.Equal("Heart", result["Heart"]); // unmatched, kept
        }

        private static string FindKeyFor(Dictionary<string, string> map, string value)
        {
            foreach (var kvp in map)
                if (kvp.Value == value) return kvp.Key;
            return null;
        }
    }
}
