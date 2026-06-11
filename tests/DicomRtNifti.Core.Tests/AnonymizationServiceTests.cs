using System.IO;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Tests for the per-identifier anonymization model: deterministic, namespaced, type-prefixed
    /// hashes; persistence/reuse across sessions; and honoring manual overrides from the key file
    /// (so the anonymization-key editor's "pin a specific output hash" works).
    /// </summary>
    public class AnonymizationServiceTests
    {
        [Fact]
        public void Hashes_AreDeterministic_AndTypePrefixed()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            var svc = new AnonymizationService(keyPath, "salt");

            string p1 = svc.GetPatientHash("MRN-123");
            string p2 = svc.GetPatientHash("MRN-123");
            string st = svc.GetStudyHash("1.2.3");
            string se = svc.GetSeriesHash("1.2.3"); // same value as study, different namespace

            Assert.Equal(p1, p2);                 // deterministic / idempotent
            Assert.StartsWith("P", p1);
            Assert.StartsWith("ST", st);
            Assert.StartsWith("SE", se);
            Assert.NotEqual(st, se);              // namespacing keeps types from colliding
        }

        [Fact]
        public void Hashes_PersistAndReload_AcrossInstances()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");

            var first = new AnonymizationService(keyPath, "salt");
            string patientHash = first.GetPatientHash("MRN-1");
            string seriesHash = first.GetSeriesHash("1.9.9");
            first.Save();

            var reloaded = new AnonymizationService(keyPath, "salt");
            Assert.Equal(patientHash, reloaded.GetPatientHash("MRN-1"));
            Assert.Equal(seriesHash, reloaded.GetSeriesHash("1.9.9"));
        }

        [Fact]
        public void ManualOverride_InKeyFile_IsHonored_NotRecomputed()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");

            // Simulate the editor pinning a specific output hash for an MRN.
            var keyFile = new AnonymizationKeyFile { Salt = "salt" };
            keyFile.Patients["MRN-42"] = "P_custom_override";
            AnonymizationService.SaveKeyFile(keyPath, keyFile);

            var svc = new AnonymizationService(keyPath, "salt");
            Assert.Equal("P_custom_override", svc.GetPatientHash("MRN-42"));
        }
    }
}
