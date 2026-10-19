using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Where the anonymization key lives (#54): always written, so an anonymized export can be
    /// traced back, but never inside the export, because an export is shared by handing on its
    /// folder. Default <c>&lt;output&gt;.key/AnonymizationKey.json</c>; an explicit location is
    /// honoured unless it lies at or under the export root. A key an earlier export left inside
    /// the root is merged into the new location, overrides included, and reported, never deleted.
    /// </summary>
    public class AnonymizationKeyLocationTests : IDisposable
    {
        private const string Salt = "key-location-salt";
        private readonly string _dir;
        private readonly string _out;

        public AnonymizationKeyLocationTests()
        {
            _dir = DicomTestData.NewTempDir();
            _out = Path.Combine(_dir, "export");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string SiblingKey => Path.Combine(_dir, "export.key", AnonymizationKeyLocation.FileName);

        // ── resolution ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Default_IsAFolderBesideTheExport()
        {
            Assert.Equal(SiblingKey, AnonymizationKeyLocation.Resolve(_out, null));
            Assert.Equal(SiblingKey, AnonymizationKeyLocation.Resolve(_out, "  "));
            Assert.Equal(SiblingKey, AnonymizationKeyLocation.Resolve(_out + Path.DirectorySeparatorChar, null));
            Assert.False(AnonymizationKeyLocation.IsWithin(SiblingKey, _out));
        }

        [Fact]
        public void Explicit_FileIsUsedAsGiven_AndAFolderGetsTheFileName()
        {
            string file = Path.Combine(_dir, "keys", "cohortA.json");
            Assert.Equal(file, AnonymizationKeyLocation.Resolve(_out, file));

            string existingFolder = Path.Combine(_dir, "vault");
            Directory.CreateDirectory(existingFolder);
            Assert.Equal(Path.Combine(existingFolder, AnonymizationKeyLocation.FileName),
                AnonymizationKeyLocation.Resolve(_out, existingFolder));

            string newFolder = Path.Combine(_dir, "not-yet") + Path.DirectorySeparatorChar;
            Assert.Equal(Path.Combine(_dir, "not-yet", AnonymizationKeyLocation.FileName),
                AnonymizationKeyLocation.Resolve(_out, newFolder));
        }

        [Fact]
        public void Explicit_LocationInsideTheExport_IsRefused_HoweverItIsSpelled()
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            var inside = new List<string>
            {
                _out,                                                         // the root itself
                _out + sep,                                                   // ... with a trailing separator
                Path.Combine(_out, AnonymizationKeyLocation.FileName),        // the pre-#54 location
                Path.Combine(_out, "P1a2b3c4d5e", "key.json"),                // deep inside
                Path.Combine(_dir, "elsewhere", "..", "export", "key.json"),  // back in through ..
            };
            if (!OperatingSystem.IsLinux())
                inside.Add(Path.Combine(_out.ToUpperInvariant(), "key.json")); // case-insensitive volumes

            foreach (string keyPath in inside)
            {
                var ex = Assert.Throws<ArgumentException>(() => AnonymizationKeyLocation.Resolve(_out, keyPath));
                Assert.Contains("inside the export folder", ex.Message);
                Assert.Contains(SiblingKey, ex.Message); // names the default as the way out
            }
        }

        [Fact]
        public void A_SiblingThatSharesThePrefix_IsNotInside()
        {
            Assert.False(AnonymizationKeyLocation.IsWithin(Path.Combine(_dir, "export2", "k.json"), _out));
            Assert.False(AnonymizationKeyLocation.IsWithin(Path.Combine(_dir, "export.key", "k.json"), _out));
            Assert.True(AnonymizationKeyLocation.IsWithin(Path.Combine(_out, "export2", "k.json"), _out));
        }

        [Fact]
        public void A_VolumeRootExport_HasNoDefault()
        {
            string volumeRoot = Path.GetPathRoot(_dir);
            var ex = Assert.Throws<ArgumentException>(() => AnonymizationKeyLocation.DefaultFor(volumeRoot));
            Assert.Contains("root of a volume", ex.Message);
        }

        // ── opening, and the key an earlier export left inside the root ────────────────────

        [Fact]
        public void Open_WithNoLegacyKey_WritesNothingUntilSaved_AndTheKeyLandsBesideTheExport()
        {
            var anon = AnonymizationKeyLocation.Open(_out, null, Salt, out string warning);
            Assert.Null(warning);
            Assert.Equal(SiblingKey, anon.KeyFilePath);
            Assert.False(File.Exists(SiblingKey));

            string hash = anon.GetPatientHash("MRN-OPEN-1");
            anon.Save();
            Assert.True(File.Exists(SiblingKey));
            Assert.Equal(hash, AnonymizationService.LoadKeyFile(SiblingKey).Patients["MRN-OPEN-1"]);
            Assert.False(Directory.Exists(_out), "opening the key created the export folder");
        }

        [Fact]
        public void Open_CarriesALegacyKeyAcross_OverridesIncluded_AndLeavesTheOldCopy()
        {
            string legacy = Path.Combine(_out, AnonymizationKeyLocation.FileName);
            Directory.CreateDirectory(_out);
            AnonymizationService.SaveKeyFile(legacy, new AnonymizationKeyFile
            {
                Salt = Salt,
                Patients = new Dictionary<string, string> { ["MRN-LEGACY-1"] = "P_PINNED_BY_HAND" },
                Studies = new Dictionary<string, string> { ["1.2.3.4"] = "ST_PINNED" },
                Series = new Dictionary<string, string>(),
            });

            var anon = AnonymizationKeyLocation.Open(_out, null, Salt, out string warning);

            // The pinned pseudonym survives the move: tracking back still works for the old export.
            Assert.Equal("P_PINNED_BY_HAND", anon.GetPatientHash("MRN-LEGACY-1"));
            Assert.Equal("ST_PINNED", anon.GetStudyHash("1.2.3.4"));
            var moved = AnonymizationService.LoadKeyFile(SiblingKey);
            Assert.Equal("P_PINNED_BY_HAND", moved.Patients["MRN-LEGACY-1"]);
            Assert.Equal(Salt, moved.Salt);

            Assert.NotNull(warning);
            Assert.Contains(legacy, warning);
            Assert.Contains(SiblingKey, warning);
            Assert.DoesNotContain("MRN-LEGACY-1", warning);
            Assert.True(File.Exists(legacy), "the legacy key was deleted; it may be the only copy");
        }

        [Fact]
        public void Open_MergesALegacyKeyIntoAnExistingOne_AndIsIdempotent()
        {
            AnonymizationService.SaveKeyFile(SiblingKey, new AnonymizationKeyFile
            {
                Salt = Salt,
                Patients = new Dictionary<string, string> { ["MRN-NEW"] = "P_NEW" },
            });
            Directory.CreateDirectory(_out);
            AnonymizationService.SaveKeyFile(Path.Combine(_out, AnonymizationKeyLocation.FileName), new AnonymizationKeyFile
            {
                Salt = Salt,
                Patients = new Dictionary<string, string> { ["MRN-OLD"] = "P_OLD", ["MRN-NEW"] = "P_NEW" },
            });

            AnonymizationKeyLocation.Open(_out, null, Salt, out _);
            string once = File.ReadAllText(SiblingKey);
            AnonymizationKeyLocation.Open(_out, null, Salt, out string again);

            var merged = AnonymizationService.LoadKeyFile(SiblingKey);
            Assert.Equal("P_OLD", merged.Patients["MRN-OLD"]);
            Assert.Equal("P_NEW", merged.Patients["MRN-NEW"]);
            Assert.Equal(once, File.ReadAllText(SiblingKey));
            Assert.NotNull(again); // the old copy is still inside the export, so it is still reported
        }

        [Fact]
        public void Open_RefusesTwoPseudonymsForOneIdentifier_WithoutNamingIt()
        {
            AnonymizationService.SaveKeyFile(SiblingKey, new AnonymizationKeyFile
            {
                Salt = Salt,
                Patients = new Dictionary<string, string> { ["MRN-CLASH"] = "P_AAAAAAAAAA" },
            });
            Directory.CreateDirectory(_out);
            AnonymizationService.SaveKeyFile(Path.Combine(_out, AnonymizationKeyLocation.FileName), new AnonymizationKeyFile
            {
                Salt = Salt,
                Patients = new Dictionary<string, string> { ["MRN-CLASH"] = "P_BBBBBBBBBB" },
            });

            var ex = Assert.Throws<InvalidOperationException>(() => AnonymizationKeyLocation.Open(_out, null, Salt, out _));
            Assert.Contains("P_AAAAAAAAAA", ex.Message);
            Assert.Contains("P_BBBBBBBBBB", ex.Message);
            Assert.DoesNotContain("MRN-CLASH", ex.Message);
        }

        [Fact]
        public void Open_RefusesALegacyKeyRecordedUnderAnotherSalt()
        {
            Directory.CreateDirectory(_out);
            string legacy = Path.Combine(_out, AnonymizationKeyLocation.FileName);
            AnonymizationService.SaveKeyFile(legacy, new AnonymizationKeyFile
            {
                Salt = "another-salt",
                Patients = new Dictionary<string, string> { ["MRN-1"] = "P_X" },
            });

            var ex = Assert.Throws<InvalidOperationException>(() => AnonymizationKeyLocation.Open(_out, null, Salt, out _));
            Assert.Contains(legacy, ex.Message);
            Assert.False(File.Exists(SiblingKey));
        }

        // ── the Core guard ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task CohortExport_RefusesAKeyInsideTheExport_BeforeWritingAnything()
        {
            var options = new CohortExportOptions { InputRoot = _dir, OutputRoot = _out, Anonymize = true, Salt = Salt };
            var anon = new AnonymizationService(Path.Combine(_out, AnonymizationKeyLocation.FileName), Salt);
            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExecuteAsync(new CohortExportPlan(), options, anon, null, CancellationToken.None));
            Assert.Contains("inside the export folder", ex.Message);
            Assert.False(Directory.Exists(_out), "the export folder was written before the refusal");
        }

        [Fact]
        public async Task CohortExport_ReportsTheKeysFullPath()
        {
            var options = new CohortExportOptions { InputRoot = _dir, OutputRoot = _out, Anonymize = true, Salt = Salt };
            var anon = AnonymizationKeyLocation.Open(_out, null, Salt, out _);
            var result = await new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
                .ComputeManifestAsync(new CohortExportPlan(), options, anon, null, CancellationToken.None);

            Assert.Equal(SiblingKey, result.AnonymizationKey);
            Assert.True(File.Exists(SiblingKey));
            Assert.Empty(Directory.GetFiles(_out, AnonymizationKeyLocation.FileName, SearchOption.AllDirectories));
        }
    }

    /// <summary>The CLI side of #54: the default location, --key-path, and the exit-2 refusals.</summary>
    [Collection(CliConsoleCollection.Name)]
    public class CliAnonymizationKeyPathTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _input;
        private readonly string _out;

        public CliAnonymizationKeyPathTests()
        {
            _dir = DicomTestData.NewTempDir();
            _input = Path.Combine(_dir, "in");
            _out = Path.Combine(_dir, "out");

            string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(Path.Combine(_input, "CT"), series, frame,
                sliceCount: 3, size: 12, studyUid: study, patientId: "MRN-CLI-KEY");
            DicomTestData.WriteRtStructWithContours(Path.Combine(_input, "RS"), "rs.dcm", study, DicomTestData.NewUid(), frame,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("GTV", DicomTestData.SquareContour(2, 8, 1)),
                },
                patientId: "MRN-CLI-KEY", referencedSeriesUid: series);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void CohortConvert_Anonymized_WritesTheKeyBesideTheExport_AndReportsItsPath()
        {
            var run = CliRun.Execute("--cohort-convert", "--input", _input, "--output", _out,
                "--anonymize", "--salt", "cli-key-salt", "--no-doses");
            Assert.True(run.ExitCode == 0, run.ToString());

            string key = Path.Combine(_dir, "out.key", AnonymizationKeyLocation.FileName);
            Assert.Equal(key, (string)JObject.Parse(run.Stdout)["anonymization_key"]);
            Assert.Contains("MRN-CLI-KEY", File.ReadAllText(key));
            Assert.Empty(Directory.GetFiles(_out, AnonymizationKeyLocation.FileName, SearchOption.AllDirectories));
            Assert.Contains($"Anonymization key: {key}", run.Stderr);
        }

        [Fact]
        public void CohortConvert_KeyPathFolder_PutsTheKeyThere()
        {
            string vault = Path.Combine(_dir, "vault");
            Directory.CreateDirectory(vault);
            var run = CliRun.Execute("--cohort-manifest", "--input", _input, "--output", _out,
                "--anonymize", "--salt", "cli-key-salt", "--no-volumes", "--key-path", vault);
            Assert.True(run.ExitCode == 0, run.ToString());

            string key = Path.Combine(vault, AnonymizationKeyLocation.FileName);
            Assert.Equal(key, (string)JObject.Parse(run.Stdout)["anonymization_key"]);
            Assert.True(File.Exists(key));
            Assert.False(Directory.Exists(Path.Combine(_dir, "out.key")));
        }

        [Fact]
        public void CohortConvert_KeyPathInsideTheOutput_Exits2_BeforeWritingAnything()
        {
            var run = CliRun.Execute("--cohort-convert", "--input", _input, "--output", _out,
                "--anonymize", "--key-path", Path.Combine(_out, "keys", "key.json"));
            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Contains("inside the export folder", run.Stderr);
            Assert.DoesNotContain("   at ", run.Stderr);
            Assert.False(Directory.Exists(_out), "the export folder was written before the refusal");
        }

        [Fact]
        public void CohortConvert_KeyPathWithoutAnonymize_Exits2()
        {
            var run = CliRun.Execute("--cohort-convert", "--input", _input, "--output", _out,
                "--key-path", Path.Combine(_dir, "vault"));
            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Contains("--key-path", run.Stderr);
            Assert.Contains("--anonymize", run.Stderr);
        }
    }
}
