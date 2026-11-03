using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The cohort command-line grammar moved into Core with a renderer beside the parser, so a
    /// front-end that builds options in a form can show the command that reproduces them. The
    /// pact these pin: render then parse gives back equal options, for every field of
    /// <see cref="CohortExportOptions"/>, enumerated by reflection so a field added there without
    /// a flag here fails.
    /// </summary>
    public class CohortCommandLineTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Theory]
        [InlineData(CohortMode.Manifest)]
        [InlineData(CohortMode.Convert)]
        public void RenderThenParse_GivesBackEveryField(CohortMode mode)
        {
            var command = Everything(mode);

            string[] args = CohortCommandLine.Render(command, CohortDocuments.Identity);
            var parsed = CohortCommandLine.Parse(args);

            Assert.Equal(mode, parsed.Mode);
            Assert.Equal(Snapshot(command.Options), Snapshot(parsed.Options));
            Assert.Equal(command.AssociationsPath, parsed.AssociationsPath);
            Assert.Equal(command.JsonOutPath, parsed.JsonOutPath);
        }

        /// <summary>
        /// Between them the two commands above set every settable option away from its default,
        /// so every option has been through a round trip. A new option fails here until the
        /// grammar can carry it and <see cref="Everything"/> sets it.
        /// </summary>
        [Fact]
        public void TheRoundTrips_CoverEveryOption()
        {
            var defaults = Snapshot(new CohortExportOptions());
            var manifest = Snapshot(Everything(CohortMode.Manifest).Options);
            var convert = Snapshot(Everything(CohortMode.Convert).Options);

            var untouched = defaults.Keys
                .Where(k => manifest[k] == defaults[k] && convert[k] == defaults[k])
                .ToList();
            Assert.Empty(untouched);
        }

        [Fact]
        public void Scan_RoundTripsItsInputAndJsonOut()
        {
            var command = new CohortCommand
            {
                Mode = CohortMode.Scan,
                Options = new CohortExportOptions { InputRoot = Path.Combine(_dir, "in") },
                JsonOutPath = Path.Combine(_dir, "scan.json"),
            };

            var parsed = CohortCommandLine.Parse(CohortCommandLine.Render(command, CohortDocuments.Identity));

            Assert.Equal(CohortMode.Scan, parsed.Mode);
            Assert.Equal(Snapshot(command.Options), Snapshot(parsed.Options));
            Assert.Equal(command.JsonOutPath, parsed.JsonOutPath);
        }

        [Fact]
        public void Render_OfDefaults_IsTheModeAndItsFolders()
        {
            var options = new CohortExportOptions { InputRoot = "in", OutputRoot = "out" };

            Assert.Equal(new[] { "--cohort-convert", "--input", "in", "--output", "out" },
                CohortCommandLine.Render(CohortMode.Convert, options, CohortDocuments.Identity));
            Assert.Equal(new[] { "--cohort-scan", "--input", "in" },
                CohortCommandLine.Render(CohortMode.Scan, new CohortExportOptions { InputRoot = "in" }, null));
        }

        [Fact]
        public void Render_PresentsEveryPath()
        {
            var command = Everything(CohortMode.Convert);

            string[] args = CohortCommandLine.Render(command, p => "<" + Path.GetFileName(p) + ">");

            foreach (var flag in new[] { "--input", "--output", "--key-path", "--associations", "--json-out" })
            {
                string value = args[Array.IndexOf(args, flag) + 1];
                Assert.StartsWith("<", value);
            }
        }

        [Fact]
        public void Render_RefusesWhatTheModeCannotSay()
        {
            var survey = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", ExportImages = false };
            var convert = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", ComputeVolumes = false };
            var keywords = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", ImageKeywords = { "KVP" } };
            var scan = new CohortExportOptions { InputRoot = "in", Anonymize = true };
            var associations = new CohortExportOptions
            {
                InputRoot = "in", OutputRoot = "out",
                Associations = { new RoiAssociation { CanonicalName = "Lung" } },
            };
            var comma = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", PatientIds = { "A,B" } };

            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Manifest, survey, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Manifest, keywords, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Convert, convert, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Scan, scan, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Convert, associations, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Convert, comma, null));
        }

        [Fact]
        public void Parse_TakesTheModesInTheirPrecedenceOrder()
        {
            Assert.Equal(CohortMode.Scan,
                CohortCommandLine.Parse(new[] { "--cohort-convert", "--cohort-scan", "--input", "x" }).Mode);
            Assert.Equal(CohortMode.Manifest,
                CohortCommandLine.Parse(new[] { "--COHORT-CONVERT", "--cohort-manifest", "--input", "x", "--output", "y" }).Mode);

            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(new[] { "--input", "x" }));
            Assert.Contains("--cohort-scan", ex.Message);
        }

        [Fact]
        public void Parse_AcceptsTargetSpacingAsAnAlias_AndRendersItAsOutputSpacing()
        {
            var parsed = CohortCommandLine.Parse(new[]
            {
                "--cohort-manifest", "--input", "in", "--output", "out", "--target-spacing", "1, 1.5 ,2.5",
            });

            Assert.Equal(new[] { 1.0, 1.5, 2.5 }, parsed.Options.OutputSpacing);
            Assert.Equal(
                new[] { "--cohort-manifest", "--input", "in", "--output", "out", "--output-spacing", "1,1.5,2.5" },
                CohortCommandLine.Render(parsed, null));
        }

        /// <summary>The same refusals, in the same order, the CLI's own parser gave.</summary>
        [Theory]
        [InlineData("Required argument '--input' is missing.", "--cohort-convert", "--output", "out")]
        [InlineData("Required argument '--output' is missing.", "--cohort-manifest", "--input", "in", "--output-spacing", "x")]
        [InlineData("--key-path places the anonymization key, so it needs --anonymize.",
            "--cohort-convert", "--input", "in", "--output", "out", "--key-path", "k", "--output-spacing", "x")]
        [InlineData("--output-spacing: spacing component 'x' is not a number",
            "--cohort-convert", "--input", "in", "--output", "out", "--output-spacing", "1,x,1")]
        [InlineData("--output-spacing: spacing component '0' must be greater than zero",
            "--cohort-manifest", "--input", "in", "--output", "out", "--target-spacing", "1,1,0")]
        public void Parse_RefusesWithTheCliMessages(string message, params string[] args)
        {
            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(args));
            Assert.Equal(message, ex.Message);
        }

        [Fact]
        public void Parse_RefusesAMissingAssociationsFile_AndLogsALoadedOne()
        {
            string missing = Path.Combine(_dir, "nope.json");
            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(new[]
            {
                "--cohort-manifest", "--input", "in", "--output", "out", "--associations", missing,
            }));
            Assert.Equal($"Associations file not found: {missing}", ex.Message);

            string file = WriteAssociations();
            var log = new CollectingProgress();
            var parsed = CohortCommandLine.Parse(new[]
            {
                "--cohort-manifest", "--input", "in", "--output", "out", "--associations", file,
            }, log);
            Assert.Equal(2, parsed.Options.Associations.Count);
            Assert.Equal(new[] { "Loaded 2 ROI association(s)" }, log.Messages);
        }

        /// <summary>
        /// Every option a mode can carry, away from its default: the manifest gets the common ones
        /// plus --no-volumes; the conversion the common ones plus its export toggles and keywords.
        /// </summary>
        private CohortCommand Everything(CohortMode mode)
        {
            var options = new CohortExportOptions
            {
                InputRoot = Path.Combine(_dir, "in"),
                OutputRoot = Path.Combine(_dir, "out"),
                PatientIds = new List<string> { "PAT_A", "PAT-B 2" },
                SeriesDescriptionFilter = "planning CT",
                StructDescriptionFilter = "--looks-like-a-flag",
                PreferLargestSeries = true,
                RequireStructures = true,
                RequireDose = true,
                Associations = new SettingsService().ImportAssociations(WriteAssociations()),
                OnlyAssociatedRois = true,
                OutputSpacing = new[] { 0.9765625, 1.0 / 3.0, 2.5 },
                Anonymize = true,
                KeyPath = Path.Combine(_dir, "keys") + Path.DirectorySeparatorChar,
                Salt = "a salt, with spaces",
                ManifestFileName = "survey.csv",
                FailFast = true,
            };
            if (mode == CohortMode.Convert)
            {
                options.ExportImages = false;
                options.ExportStructures = false;
                options.ExportDoses = false;
                options.ImageKeywords = new List<string> { "PatientAge", "@VoxelSize" };
                options.StructureKeywords = new List<string> { "StructureSetLabel", "@RoiNames" };
                options.DoseKeywords = new List<string> { "DoseUnits", "@MaxDose" };
            }
            else
            {
                options.ComputeVolumes = false;
            }

            return new CohortCommand
            {
                Mode = mode,
                Options = options,
                AssociationsPath = Path.Combine(_dir, "associations.json"),
                JsonOutPath = Path.Combine(_dir, "result.json"),
            };
        }

        private string WriteAssociations()
        {
            string path = Path.Combine(_dir, "associations.json");
            new SettingsService().ExportAssociations(new List<RoiAssociation>
            {
                new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "lung left", "LLung" } },
                new RoiAssociation { CanonicalName = "Heart", Aliases = { "heart" } },
            }, path);
            return path;
        }

        /// <summary>Every settable option as JSON, so lists and arrays compare by content.</summary>
        private static Dictionary<string, string> Snapshot(CohortExportOptions options)
        {
            return typeof(CohortExportOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToDictionary(p => p.Name, p => JsonConvert.SerializeObject(p.GetValue(options)));
        }
    }
}
