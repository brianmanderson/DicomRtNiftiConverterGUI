using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Regression tests for the "swallow the parse error, then overwrite the file" pattern that
    /// the anonymization key and the two settings files all shared.
    ///
    /// The failure it produced was silent and irreversible. A key file truncated by a crash no
    /// longer parsed; the loader turned that into a null return, which the caller could not tell
    /// apart from "no key file yet"; the service started with empty maps and the next Save wrote
    /// those empty maps over whatever the file still held. Every previously assigned pseudonym was
    /// gone, so the same patient came back under a second identity on the next export — the same
    /// subject silently present in two arms of a train/validation split.
    ///
    /// The tests below pin the two halves of the fix: an existing-but-unparseable file is refused
    /// rather than discarded, and the refusal leaves the file byte-for-byte intact.
    /// </summary>
    public class JsonStateFileIntegrityTests
    {
        private const string TruncatedKeyJson = "{\n  \"Salt\": \"salt\",\n  \"Patients\": {\n    \"MRN-1\": \"P0011";

        [Fact]
        public void AnonymizationService_Ctor_Throws_WhenKeyFileExistsButDoesNotParse()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, TruncatedKeyJson);

            var ex = Assert.Throws<InvalidDataException>(
                () => new AnonymizationService(keyPath, "salt"));

            // The message has to name the file, because moving it aside is the only recovery.
            Assert.Contains(keyPath, ex.Message);
        }

        /// <summary>
        /// The empty file is the classic truncation artefact: it deserializes to null without
        /// throwing, so it needs its own guard or it slips straight back through as "no key yet".
        /// </summary>
        [Fact]
        public void AnonymizationService_Ctor_Throws_WhenKeyFileIsEmpty()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, "");

            Assert.Throws<InvalidDataException>(() => new AnonymizationService(keyPath, "salt"));
        }

        /// <summary>
        /// A key file that parses cleanly but carries no mapping section is damaged in exactly
        /// the same way, and used to be just as destructive: every map fell through to
        /// `?? new Dictionary&lt;&gt;()`, so the service started empty and the first Save() replaced
        /// the file. The original null-check caught an empty file and a JSON null but not this.
        /// </summary>
        [Fact]
        public void AnonymizationService_Ctor_Throws_WhenKeyFileHasNoMappingSection()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, "{\"Salt\": \"salt\"}");

            Assert.Throws<InvalidDataException>(() => new AnonymizationService(keyPath, "salt"));
        }

        /// <summary>
        /// The destructive consequence of the above, end to end: the recorded override must
        /// survive a construct/hash/save cycle rather than the patient being re-exported under
        /// a second pseudonym.
        /// </summary>
        [Fact]
        public void KeyFileWithNoMappingSection_IsPreserved_NotOverwritten()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            const string original = "{\"Salt\": \"salt\"}";
            File.WriteAllText(keyPath, original);

            Assert.Throws<InvalidDataException>(() => new AnonymizationService(keyPath, "salt"));
            Assert.Equal(original, File.ReadAllText(keyPath));
        }

        /// <summary>
        /// The other complement: a run that legitimately converts nothing writes all three
        /// sections empty. That file is intact, not damaged, and must still load — which is why
        /// the check tests for the presence of the sections rather than for any mappings in them.
        /// </summary>
        [Fact]
        public void KeyFileWithPresentButEmptySections_StillLoads()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, "{\"Salt\": \"salt\", \"Patients\": {}, \"Studies\": {}, \"Series\": {}}");

            var svc = new AnonymizationService(keyPath, "salt");

            Assert.NotNull(svc.GetPatientHash("MRN-1"));
        }

        /// <summary>
        /// The complement, and the reason the check requires all three sections to be absent:
        /// hand-authoring a key that pins only patient overrides is a supported workflow, so it
        /// must still load.
        /// </summary>
        [Fact]
        public void KeyFileWithOnlyPatientOverrides_StillLoads()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, "{\"Salt\": \"salt\", \"Patients\": {\"MRN-1\": \"SUBJ_01\"}}");

            var svc = new AnonymizationService(keyPath, "salt");

            Assert.Equal("SUBJ_01", svc.GetPatientHash("MRN-1"));
        }

        /// <summary>
        /// The bug itself: construct over a damaged key, hash a patient, save — and the damaged
        /// file is replaced by a fresh one holding only this run's mapping. Before the fix this
        /// assertion failed with the file rewritten as valid-but-empty JSON.
        /// </summary>
        [Fact]
        public void DamagedKeyFile_IsPreserved_NotOverwrittenWithAFreshOne()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, TruncatedKeyJson);
            byte[] before = File.ReadAllBytes(keyPath);

            try
            {
                var svc = new AnonymizationService(keyPath, "salt");
                svc.GetPatientHash("MRN-2");
                svc.Save();
            }
            catch (InvalidDataException)
            {
                // Expected — the point of the test is what is on disk afterwards.
            }

            Assert.Equal(before, File.ReadAllBytes(keyPath));
        }

        // ---------- salt mismatch ----------

        /// <summary>
        /// The salt is half of every hash in the key file. Accepting a different one and letting
        /// Save() stamp it over the recorded value produced a key that no longer reproduced its own
        /// entries, and an export split across two unlinkable pseudonym spaces.
        /// </summary>
        [Fact]
        public void Ctor_Throws_WhenSuppliedSaltDiffersFromTheRecordedOne()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            var keyFile = new AnonymizationKeyFile { Salt = "original-salt" };
            keyFile.Patients["MRN-1"] = "Pdeadbeef01";
            AnonymizationService.SaveKeyFile(keyPath, keyFile);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new AnonymizationService(keyPath, "different-salt"));

            Assert.Contains("original-salt", ex.Message);
            Assert.Contains("different-salt", ex.Message);
        }

        /// <summary>
        /// Refusing must not also rewrite the recorded salt — that was the second half of the bug.
        /// </summary>
        [Fact]
        public void RecordedSalt_SurvivesAMismatchedRun()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            AnonymizationService.SaveKeyFile(keyPath, new AnonymizationKeyFile { Salt = "original-salt" });

            try
            {
                var svc = new AnonymizationService(keyPath, "different-salt");
                svc.GetPatientHash("MRN-9");
                svc.Save();
            }
            catch (InvalidOperationException)
            {
                // Expected; the assertion is about the file.
            }

            Assert.Equal("original-salt", AnonymizationService.LoadKeyFile(keyPath).Salt);
        }

        /// <summary>
        /// The guard lived only on the constructor, and the anonymization-key editor never builds
        /// one: it fills an AnonymizationKeyFile with the salt from settings and calls the static
        /// SaveKeyFile. Opening a key recorded under salt A while settings named salt B therefore
        /// rewrote the recorded salt to B, over hashes every one of which was built with A — the
        /// key no longer reproduced its own entries, and nothing said so.
        /// </summary>
        [Fact]
        public void SaveKeyFile_Refuses_ToStampADifferentSaltOverAnExistingKey()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            var original = new AnonymizationKeyFile { Salt = "original-salt" };
            original.Patients["MRN-1"] = "Pdeadbeef01";
            AnonymizationService.SaveKeyFile(keyPath, original);
            byte[] before = File.ReadAllBytes(keyPath);

            // What the editor does: same mappings, salt taken from settings.
            var edited = new AnonymizationKeyFile { Salt = "settings-salt" };
            edited.Patients["MRN-1"] = "Pdeadbeef01";

            var ex = Assert.Throws<InvalidOperationException>(
                () => AnonymizationService.SaveKeyFile(keyPath, edited));

            Assert.Contains("original-salt", ex.Message);
            Assert.Contains("settings-salt", ex.Message);
            Assert.Equal(before, File.ReadAllBytes(keyPath));
        }

        /// <summary>Re-saving under the recorded salt is the normal case and must still work.</summary>
        [Fact]
        public void SaveKeyFile_AcceptsAnEditUnderTheRecordedSalt()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            AnonymizationService.SaveKeyFile(keyPath, new AnonymizationKeyFile { Salt = "s" });

            var edited = new AnonymizationKeyFile { Salt = "s" };
            edited.Patients["MRN-1"] = "SUBJ_01";
            AnonymizationService.SaveKeyFile(keyPath, edited);

            Assert.Equal("SUBJ_01", AnonymizationService.LoadKeyFile(keyPath).Patients["MRN-1"]);
        }

        /// <summary>
        /// The other bypass: EnsureSaltMatches returned early on a null/blank recorded salt, and
        /// Save() then stamped the caller's salt over it. A key holding a patient hashed under the
        /// blank salt came back claiming the new one — two patients, two salts, one recorded.
        /// Reachable from a hand-edited "Salt": null and from "HashSalt": "" in settings.
        /// </summary>
        [Theory]
        [InlineData("{\"Salt\": null, \"Patients\": {\"MRN-1\": \"Pblank01\"}}")]
        [InlineData("{\"Salt\": \"\", \"Patients\": {\"MRN-1\": \"Pblank01\"}}")]
        public void ABlankRecordedSalt_IsCheckedLikeAnyOther(string json)
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, json);
            string before = File.ReadAllText(keyPath);

            Assert.Throws<InvalidOperationException>(
                () => new AnonymizationService(keyPath, "a-real-salt"));

            // And nothing was written over it on the way out.
            Assert.Equal(before, File.ReadAllText(keyPath));
        }

        /// <summary>
        /// The complement that keeps the guard from being a nuisance: a key file that records no
        /// salt *and* no mappings has nothing hashed under anything, so the caller's salt may be
        /// adopted. Anything else would refuse a run whose only sin is an empty key.
        /// </summary>
        [Fact]
        public void ABlankRecordedSalt_WithNoMappings_IsAdopted()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            File.WriteAllText(keyPath, "{\"Salt\": \"\", \"Patients\": {}, \"Studies\": {}, \"Series\": {}}");

            var svc = new AnonymizationService(keyPath, "a-real-salt");
            svc.GetPatientHash("MRN-1");
            svc.Save();

            Assert.Equal("a-real-salt", AnonymizationService.LoadKeyFile(keyPath).Salt);
        }

        /// <summary>
        /// The check must not fire on the cases that are fine: the same salt, and a key file that
        /// omits Salt entirely — those deserialize to the same "DicomToNifti" the services default
        /// to, so an unconfigured install keeps loading its own history.
        /// </summary>
        [Fact]
        public void MatchingSalt_IsAccepted()
        {
            string dir = DicomTestData.NewTempDir();

            string matching = Path.Combine(dir, "matching.json");
            AnonymizationService.SaveKeyFile(matching, new AnonymizationKeyFile { Salt = "s" });
            Assert.NotNull(new AnonymizationService(matching, "s"));

            string noSaltField = Path.Combine(dir, "no-salt-field.json");
            File.WriteAllText(noSaltField, "{\"Patients\": {\"MRN-1\": \"Pabc\"}}");
            Assert.Equal("Pabc", new AnonymizationService(noSaltField, null).GetPatientHash("MRN-1"));
        }

        [Fact]
        public void LoadKeyFile_ReturnsNull_WhenNoFileExists()
        {
            string keyPath = Path.Combine(DicomTestData.NewTempDir(), "AnonymizationKey.json");
            Assert.Null(AnonymizationService.LoadKeyFile(keyPath));
        }

        /// <summary>
        /// SettingsService's public entry points are hard-wired to %AppData%, so the refusal is
        /// exercised through the shared reader the two of them delegate to.
        /// </summary>
        [Theory]
        [InlineData("{\"HashSalt\": \"abc\"")]   // truncated mid-object
        [InlineData("")]                          // truncated to nothing
        [InlineData("null")]
        public void SettingsService_Read_Throws_WhenFileExistsButDoesNotParse(string content)
        {
            string path = Path.Combine(DicomTestData.NewTempDir(), "settings.json");
            File.WriteAllText(path, content);

            var ex = Assert.Throws<InvalidDataException>(
                () => SettingsService.ReadJson<AppSettings>(path, "application settings"));

            Assert.Contains(path, ex.Message);
        }

        [Fact]
        public void SettingsService_Read_Throws_WhenAssociationsFileDoesNotParse()
        {
            string path = Path.Combine(DicomTestData.NewTempDir(), "roi_associations.json");
            File.WriteAllText(path, "[{\"CanonicalName\": \"Lung\"");

            Assert.Throws<InvalidDataException>(
                () => SettingsService.ReadJson<List<RoiAssociation>>(path, "ROI associations"));
        }

        [Fact]
        public void SettingsService_Read_RoundTripsAValidFile()
        {
            string path = Path.Combine(DicomTestData.NewTempDir(), "roi_associations.json");
            File.WriteAllText(path, "[{\"CanonicalName\": \"Lung\", \"Aliases\": [\"Lungs\"]}]");

            var loaded = SettingsService.ReadJson<List<RoiAssociation>>(path, "ROI associations");

            Assert.Single(loaded);
            Assert.Equal("Lung", loaded[0].CanonicalName);
        }

        // ---------- atomic write ----------

        [Fact]
        public void AtomicWrite_ReplacesContentAndLeavesNoTemporaryFiles()
        {
            string dir = DicomTestData.NewTempDir();
            string path = Path.Combine(dir, "state.json");

            AtomicFileWriter.WriteAllText(path, "{\"a\":1}");
            AtomicFileWriter.WriteAllText(path, "{\"b\":2}");

            Assert.Equal("{\"b\":2}", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
        }

        /// <summary>
        /// A previous run that died between writing the temp file and replacing the destination
        /// leaves an orphan behind. The next write must not trip over it — a fixed temp name would.
        /// </summary>
        [Fact]
        public void AtomicWrite_SucceedsWhenAStaleTempFileIsPresent()
        {
            string dir = DicomTestData.NewTempDir();
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"old\":true}");
            File.WriteAllText(path + ".tmp-deadbeef", "garbage from a crashed run");

            AtomicFileWriter.WriteAllText(path, "{\"new\":true}");

            Assert.Equal("{\"new\":true}", File.ReadAllText(path));
        }

        [Fact]
        public void AtomicWrite_CreatesMissingDirectories()
        {
            string path = Path.Combine(DicomTestData.NewTempDir(), "nested", "deeper", "state.json");

            AtomicFileWriter.WriteAllText(path, "{}");

            Assert.Equal("{}", File.ReadAllText(path));
        }
    }
}
