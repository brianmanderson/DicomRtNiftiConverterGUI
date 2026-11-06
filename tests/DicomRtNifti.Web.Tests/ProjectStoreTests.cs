using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// What a project and a user keep on the server (plan section 3.5): association sets stored as
    /// the exact JSON <c>--associations</c> reads, an import that does not parse replacing nothing,
    /// the project's random salt (D-36) drawn once, recorded only in its key and never the public
    /// default, presets that round-trip the export toggles and never carry a salt, and the command
    /// builder's grammar, refusals and salt mask.
    /// </summary>
    public sealed class ProjectStoreTests : IDisposable
    {
        private readonly JobFolders _folders = JobFolders.Create();
        private readonly ProjectStore _store;
        private readonly UserPreferences _preferences;
        private readonly ExportCommandBuilder _builder;

        public ProjectStoreTests()
        {
            var options = _folders.Options();
            var roots = DataRoots.From(options);
            var resolver = new DataRootResolver(roots);
            _store = new ProjectStore(roots, resolver, new ProjectLocks(resolver, roots));
            _preferences = new UserPreferences(roots, resolver);
            _builder = new ExportCommandBuilder(_store, resolver,
                new RootPathPresenter(roots, Microsoft.Extensions.Options.Options.Create(options)));
            Directory.CreateDirectory(Path.Combine(_folders.Data, "cohort"));
        }

        public void Dispose() => _folders.Dispose();

        private static List<RoiAssociation> Set() => new List<RoiAssociation>
        {
            new RoiAssociation { CanonicalName = "SpinalCord", Aliases = new List<string> { "Spinal_Cord", "spinal cord" } },
            new RoiAssociation { CanonicalName = "Parotid_L", Aliases = new List<string> { "L Parotid" } },
        };

        [Fact]
        public void An_association_set_is_saved_as_the_json_the_cli_reads_and_downloads_as_those_bytes()
        {
            _store.SaveAssociations("proj", "head and neck", Set());

            string file = Path.Combine(_folders.State, "projects", "proj", "associations", "head and neck", "roi_associations.json");
            Assert.True(File.Exists(file));
            Assert.Equal("projects/proj/associations/head and neck/roi_associations.json", ProjectStore.AssociationsFile("proj", "head and neck"));
            Assert.Equal(new[] { "head and neck" }, _store.AssociationSets("proj"));
            Assert.True(_store.AssociationSetExists("proj", "head and neck"));

            // The CLI's own reader takes the file, and the download is its bytes.
            var read = new SettingsService().ImportAssociations(file);
            Assert.Equal(Set().Select(a => a.CanonicalName), read.Select(a => a.CanonicalName));
            Assert.Equal(Set()[0].Aliases, read[0].Aliases);
            Assert.Equal(File.ReadAllText(file, Encoding.UTF8), ProjectStore.ToJson(_store.LoadAssociations("proj", "head and neck")));

            // And what Core writes for an export of the same list is the same file.
            string exported = Path.Combine(_folders.Root, "exported.json");
            new SettingsService().ExportAssociations(Set(), exported);
            Assert.Equal(File.ReadAllBytes(exported), File.ReadAllBytes(file));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"CanonicalName\": \"x\"}")]
        [InlineData("[{\"Aliases\": [\"a\"]}]")]
        public void An_import_that_is_not_an_association_set_is_refused(string json)
        {
            Assert.False(ProjectStore.TryParse(json, out var associations, out string error));
            Assert.Empty(associations);
            Assert.StartsWith("The file is not an association set", error);
        }

        [Fact]
        public void An_import_parses_as_core_reads_it()
        {
            string json = ProjectStore.ToJson(Set());

            Assert.True(ProjectStore.TryParse(json, out var associations, out string error), error);
            Assert.Equal(json, ProjectStore.ToJson(associations));
            Assert.True(ProjectStore.TryParse("[{\"CanonicalName\": \"GTV\"}]", out var bare, out _));
            Assert.Empty(bare[0].Aliases);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" lead")]
        [InlineData("trail ")]
        [InlineData(".hidden")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("..")]
        [InlineData("x:y")]
        public void A_set_name_that_is_not_one_plain_name_is_refused(string name)
        {
            Assert.NotNull(ProjectStore.CheckSetName(name));
            if (name != null)
                Assert.Throws<ArgumentException>(() => _store.SaveAssociations("proj", name, Set()));
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("a/b")]
        [InlineData("")]
        public void A_project_name_that_is_not_one_folder_is_refused(string project)
        {
            Assert.NotNull(_store.CheckProjectName(project));
            Assert.Throws<ArgumentException>(() => _store.SaveAssociations(project, "set", Set()));
        }

        [Fact]
        public void A_set_that_does_not_parse_is_refused_not_replaced()
        {
            _store.SaveAssociations("proj", "broken", Set());
            string file = Path.Combine(_folders.State, "projects", "proj", "associations", "broken", "roi_associations.json");
            File.WriteAllText(file, "{ truncated");

            Assert.Throws<InvalidDataException>(() => _store.LoadAssociations("proj", "broken"));
            Assert.Equal("{ truncated", File.ReadAllText(file));
        }

        [Fact]
        public void A_projects_salt_is_random_drawn_once_and_recorded_only_in_its_key()
        {
            var pending = _store.SaltFor("proj", create: false);
            Assert.True(pending.Pending);
            Assert.Null(pending.Salt);
            string key = Path.Combine(_folders.Keys, "proj", AnonymizationKeyLocation.FileName);
            Assert.False(File.Exists(key));

            var created = _store.SaltFor("proj", create: true);
            Assert.Null(created.Problem);
            Assert.Matches("^[0-9a-f]{32}$", created.Salt);
            Assert.Equal(created.Salt, AnonymizationService.LoadKeyFile(key).Salt);
            Assert.Empty(AnonymizationService.LoadKeyFile(key).Patients);

            Assert.Equal(created.Salt, _store.SaltFor("proj", create: true).Salt);
            Assert.Equal(created.Salt, _store.SaltFor("proj", create: false).Salt);
            Assert.NotEqual(created.Salt, _store.SaltFor("other", create: true).Salt);

            // Recorded only in the key: not in the state folder, not in the output.
            foreach (string file in Directory.GetFiles(_folders.Root, "*", SearchOption.AllDirectories).Where(f => f != key))
                Assert.DoesNotContain(created.Salt, File.ReadAllText(file));
        }

        [Fact]
        public void A_key_under_the_public_default_salt_is_refused_never_used()
        {
            string key = Path.Combine(_folders.Keys, "legacy", AnonymizationKeyLocation.FileName);
            AnonymizationService.SaveKeyFile(key, new AnonymizationKeyFile { Salt = AnonymizationService.DefaultSalt });

            var salt = _store.SaltFor("legacy", create: true);

            Assert.Null(salt.Salt);
            Assert.Contains("public default salt", salt.Problem);
            Assert.Equal(AnonymizationService.DefaultSalt, AnonymizationService.LoadKeyFile(key).Salt); // untouched
        }

        [Fact]
        public void A_key_that_does_not_parse_is_reported_not_replaced()
        {
            string key = Path.Combine(_folders.Keys, "damaged", AnonymizationKeyLocation.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(key));
            File.WriteAllText(key, "");

            var salt = _store.SaltFor("damaged", create: true);

            Assert.Null(salt.Salt);
            Assert.Contains("does not parse", salt.Problem);
            Assert.Equal("", File.ReadAllText(key));
        }

        [Fact]
        public void No_key_root_no_anonymization()
        {
            var options = _folders.Options();
            options.KeyRoot = null;
            var roots = DataRoots.From(options);
            var resolver = new DataRootResolver(roots);
            var store = new ProjectStore(roots, resolver, new ProjectLocks(resolver, roots));

            Assert.False(store.CanAnonymize);
            Assert.Contains("Web:KeyRoot", store.SaltFor("proj", create: true).Problem);
        }

        [Fact]
        public void A_preset_round_trips_the_export_toggles_and_never_carries_a_salt()
        {
            var options = new CohortExportOptions
            {
                ExportImages = false,
                ExportDoses = false,
                OnlyAssociatedRois = true,
                Anonymize = true,
                OutputSpacing = new[] { 1.5, 1.5, 3.0 },
                ImageKeywords = new List<string> { "Modality", "@VoxelSize" },
                DoseKeywords = new List<string> { "@MaxDose" },
                Salt = "a-salt-that-must-not-be-kept",
            };

            _preferences.Save("local:me", "lab default", options);

            Assert.Equal(new[] { "lab default" }, _preferences.Presets("local:me"));
            Assert.Empty(_preferences.Presets("local:someone-else"));
            string file = Path.Combine(_folders.State, "users", "local_3ame", "presets", "lab default", "settings.json");
            Assert.True(File.Exists(file));
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("a-salt-that-must-not-be-kept", text);
            Assert.DoesNotContain(AnonymizationService.DefaultSalt, text);

            var applied = new CohortExportOptions();
            UserPreferences.Apply(_preferences.Load("local:me", "lab default"), applied);
            Assert.False(applied.ExportImages);
            Assert.True(applied.ExportStructures);
            Assert.False(applied.ExportDoses);
            Assert.True(applied.OnlyAssociatedRois);
            Assert.True(applied.Anonymize);
            Assert.Equal(new[] { 1.5, 1.5, 3.0 }, applied.OutputSpacing);
            Assert.Equal(options.ImageKeywords, applied.ImageKeywords);
            Assert.Empty(applied.StructureKeywords);
            Assert.Equal(options.DoseKeywords, applied.DoseKeywords);
            Assert.Equal(new CohortExportOptions().Salt, applied.Salt); // the preset does not touch it
        }

        [Theory]
        [InlineData("local:me", "users/local_3ame")]
        [InlineData("DOMAIN\\user_1", "users/DOMAIN_5cuser_5f1")]
        [InlineData("é", "users/_c3a9")]
        public void A_user_id_is_one_folder_name_and_two_ids_never_share_one(string user, string folder)
        {
            Assert.Equal(folder, UserPreferences.UserFolder(user));
            Assert.NotEqual(UserPreferences.UserFolder("a_b"), UserPreferences.UserFolder("a:b"));
        }

        [Fact]
        public void The_command_is_cores_render_validated_by_its_parse_and_the_job_gets_it_less_its_folders()
        {
            _store.SaveAssociations("proj", "hn", Set());
            var session = new ExportSession { InputRootId = "archive", InputPath = "cohort", Project = "proj", AssociationSet = "hn" };
            session.Options.RoiSelection = new List<string> { "SpinalCord" };
            session.Options.OutputSpacing = new[] { 2.0, 2.0, 2.0 };
            session.Options.FailFast = true;

            var convert = _builder.Build(session, new NoInventorySelection(), CohortMode.Convert);

            Assert.True(convert.IsValid, string.Join(" ", convert.Problems));
            Assert.Equal(
                "DicomRtNifti.Cli --cohort-convert --input archive:cohort --output output:proj " +
                "--associations state:projects/proj/associations/hn/roi_associations.json --rois SpinalCord " +
                "--output-spacing 2,2,2 --fail-fast",
                convert.Cli);
            Assert.Equal(new[] { "--rois", "SpinalCord", "--output-spacing", "2,2,2", "--fail-fast" }, convert.Parameters.Arguments);
            Assert.Equal("projects/proj/associations/hn/roi_associations.json", convert.Parameters.AssociationsFile);
            Assert.Equal(("archive", "cohort", "proj"), (convert.Parameters.InputRootId, convert.Parameters.InputPath, convert.Parameters.Project));

            // A survey takes only its own flags: the conversion's toggles are not carried.
            session.Options.ExportImages = false;
            session.Options.ComputeVolumes = false;
            var survey = _builder.Build(session, new NoInventorySelection(), CohortMode.Manifest, dryRun: true);
            Assert.True(survey.IsValid, string.Join(" ", survey.Problems));
            Assert.StartsWith("DicomRtNifti.Cli --cohort-manifest ", survey.Cli);
            Assert.EndsWith("--fail-fast --no-volumes --dry-run", survey.Cli);
            Assert.DoesNotContain("--no-images", survey.Cli);
            Assert.DoesNotContain("--dry-run", survey.Parameters.Arguments);
        }

        [Fact]
        public void What_cores_grammar_refuses_the_page_refuses_in_its_words()
        {
            var session = new ExportSession { InputRootId = "archive", InputPath = "cohort", Project = "proj" };
            session.Options.ImageKeywords = new List<string> { "NotADicomKeyword" };

            var refused = _builder.Build(session, new NoInventorySelection(), CohortMode.Convert);

            Assert.False(refused.IsValid);
            Assert.Null(refused.Cli);
            Assert.StartsWith("--metadata-tags: ", Assert.Single(refused.Problems));
        }

        [Fact]
        public void An_incomplete_session_says_what_is_missing()
        {
            var empty = _builder.Build(new ExportSession(), new NoInventorySelection(), CohortMode.Convert);
            Assert.Equal(new[] { "Choose the cohort folder.", "Choose or name the output project." }, empty.Problems);

            var unsaved = _builder.Build(
                new ExportSession { InputRootId = "archive", InputPath = "../out", Project = "proj", AssociationSet = "never-saved" },
                new NoInventorySelection(), CohortMode.Convert);
            Assert.Single(unsaved.Problems); // the folder refusal stops before the set is looked at
            var noSet = _builder.Build(
                new ExportSession { InputRootId = "archive", InputPath = "cohort", Project = "proj", AssociationSet = "never-saved" },
                new NoInventorySelection(), CohortMode.Convert);
            Assert.Equal("The association set 'never-saved' has not been saved in project proj.", Assert.Single(noSet.Problems));
        }

        [Fact]
        public void The_selection_from_inventory_reaches_cores_selection_flags()
        {
            var session = new ExportSession { InputRootId = "archive", InputPath = "cohort", Project = "proj" };

            var convert = _builder.Build(session, new FixedSelection(), CohortMode.Convert);

            Assert.True(convert.IsValid, string.Join(" ", convert.Problems));
            Assert.Contains("--patients P1,P2 --series 1.2.3 --struct-series 1.2.4", convert.Cli);
        }

        [Fact]
        public void The_salt_is_masked_in_a_shown_command_only()
        {
            var argv = new[] { "--cohort-convert", "--anonymize", "--salt", "0123abcd" };

            Assert.Equal(new[] { "--cohort-convert", "--anonymize", "--salt", ExportCommandBuilder.SaltToken }, ExportCommandBuilder.MaskSalt(argv));
            Assert.Equal("0123abcd", argv[3]);
        }

        [Fact]
        public void A_copy_of_the_options_shares_nothing_and_misses_no_property()
        {
            var options = new CohortExportOptions
            {
                InputRoot = "in", OutputRoot = "out", PatientIds = new List<string> { "P" }, SeriesInstanceUids = new List<string> { "S" },
                SeriesDescriptionFilter = "CT", StructDescriptionFilter = "RS", StructureSetUids = new List<string> { "R" },
                PreferLargestSeries = true, RequireStructures = true, RequireDose = true, ExportImages = false, ExportStructures = false,
                ExportDoses = false, Associations = new List<RoiAssociation> { new RoiAssociation { CanonicalName = "A", Aliases = new List<string> { "a" } } },
                OnlyAssociatedRois = true, RoiSelection = new List<string> { "A" }, OutputSpacing = new[] { 1.0, 2.0, 3.0 }, Anonymize = true,
                KeyPath = "k", Salt = "s", ImageKeywords = new List<string> { "Modality" }, StructureKeywords = new List<string> { "@RoiCount" },
                DoseKeywords = new List<string> { "@MaxDose" }, ManifestFileName = "m.csv", ComputeVolumes = false, FailFast = true,
            };

            var copy = ExportCommandBuilder.Copy(options);

            foreach (var property in typeof(CohortExportOptions).GetProperties().Where(p => p.CanWrite))
                Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(property.GetValue(options)), Newtonsoft.Json.JsonConvert.SerializeObject(property.GetValue(copy)));
            copy.PatientIds.Add("Q");
            copy.OutputSpacing[0] = 9;
            copy.Associations[0].Aliases.Add("b");
            Assert.Single(options.PatientIds);
            Assert.Equal(1.0, options.OutputSpacing[0]);
            Assert.Single(options.Associations[0].Aliases);
        }

        private sealed class FixedSelection : IInventorySelection
        {
            public IReadOnlyList<string> PatientIds => new[] { "P1", "P2" };

            public IReadOnlyList<string> SeriesInstanceUids => new[] { "1.2.3" };

            public IReadOnlyList<string> StructureSetUids => new[] { "1.2.4" };
        }
    }
}
