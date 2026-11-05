using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// --cohort-analyze's grammar, in Core's <see cref="CohortCommandLine"/>: render then parse gives
    /// back the command, the tiers not yet built are refused before any scan, and the other modes
    /// refuse what only the analysis can say.
    /// </summary>
    public class CohortAnalyzeCommandLineTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void RenderThenParse_GivesBackEveryField()
        {
            string associations = Path.Combine(_dir, "associations.json");
            new SettingsService().ExportAssociations(new List<RoiAssociation>
            {
                new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "lung left" } },
            }, associations);
            var command = new CohortCommand
            {
                Mode = CohortMode.Analyze,
                Options = new CohortExportOptions
                {
                    InputRoot = Path.Combine(_dir, "in"),
                    Anonymize = true,
                    Salt = "a salt, with spaces",
                    KeyPath = Path.Combine(_dir, "key.json"),
                    Associations = new SettingsService().ImportAssociations(associations),
                },
                AssociationsPath = associations,
                JsonOutPath = Path.Combine(_dir, "analysis.json"),
                CsvOutPath = Path.Combine(_dir, "tables"),
            };

            string[] args = CohortCommandLine.Render(command, CohortDocuments.Identity);
            var parsed = CohortCommandLine.Parse(args);

            Assert.Equal(CohortMode.Analyze, parsed.Mode);
            Assert.Equal(AnalysisTier.Header, parsed.Tier);
            Assert.Equal(JsonConvert.SerializeObject(command.Options), JsonConvert.SerializeObject(parsed.Options));
            Assert.Equal(command.AssociationsPath, parsed.AssociationsPath);
            Assert.Equal(command.JsonOutPath, parsed.JsonOutPath);
            Assert.Equal(command.CsvOutPath, parsed.CsvOutPath);
        }

        [Fact]
        public void Render_OfDefaults_IsTheModeAndItsInput()
        {
            Assert.Equal(new[] { "--cohort-analyze", "--input", "in" },
                CohortCommandLine.Render(CohortMode.Analyze, new CohortExportOptions { InputRoot = "in" }, null));
        }

        [Fact]
        public void Parse_AcceptsTheHeaderTierByName_InAnyCase()
        {
            Assert.Equal(AnalysisTier.Header, CohortCommandLine.Parse(new[] { "--cohort-analyze", "--input", "x", "--tier", "Header" }).Tier);
            Assert.Equal(AnalysisTier.Header, CohortCommandLine.Parse(new[] { "--cohort-analyze", "--input", "x" }).Tier);
        }

        [Fact]
        public void Parse_AcceptsTheContoursTier_AndRenderCarriesIt()
        {
            var command = CohortCommandLine.Parse(new[] { "--cohort-analyze", "--input", "x", "--tier", "Contours" });

            Assert.Equal(AnalysisTier.Contours, command.Tier);
            Assert.Equal(new[] { "--cohort-analyze", "--input", "x", "--tier", "contours" }, CohortCommandLine.Render(command, null));
        }

        [Theory]
        [InlineData("--tier raster is not yet available; only --tier header and --tier contours are.", "--cohort-analyze", "--input", "x", "--tier", "Raster")]
        [InlineData("--tier: expected header, contours or raster; got 'voxels'.", "--cohort-analyze", "--input", "x", "--tier", "voxels")]
        [InlineData("Required argument '--input' is missing.", "--cohort-analyze", "--tier", "contours")]
        [InlineData("--key-path places the anonymization key, so it needs --anonymize.", "--cohort-analyze", "--input", "x", "--key-path", "k")]
        public void Parse_Refuses(string message, params string[] args)
        {
            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(args));
            Assert.Equal(message, ex.Message);
        }

        [Fact]
        public void Parse_RefusesAMissingAssociationsFile()
        {
            string missing = Path.Combine(_dir, "nope.json");
            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(new[]
            {
                "--cohort-analyze", "--input", "x", "--associations", missing,
            }));
            Assert.Equal($"Associations file not found: {missing}", ex.Message);
        }

        [Fact]
        public void Render_RefusesWhatTheAnalysisCannotSay_AndTheOtherModesRefuseItsFlags()
        {
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(new CohortCommand
            {
                Mode = CohortMode.Analyze, Options = { InputRoot = "in", OutputRoot = "out" },
            }, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(new CohortCommand
            {
                Mode = CohortMode.Analyze, Options = { InputRoot = "in", PatientIds = { "A" } },
            }, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(new CohortCommand
            {
                Mode = CohortMode.Analyze, Options = { InputRoot = "in" }, Tier = AnalysisTier.Raster,
            }, null));

            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(new CohortCommand
            {
                Mode = CohortMode.Scan, Options = { InputRoot = "in" }, CsvOutPath = "tables",
            }, null));
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(new CohortCommand
            {
                Mode = CohortMode.Convert, Options = { InputRoot = "in", OutputRoot = "out" }, Tier = AnalysisTier.Raster,
            }, null));
        }

        /// <summary>Appended last, so a command line naming another cohort mode as well still picks that one.</summary>
        [Fact]
        public void TheAnalysis_IsTheLastCohortModeInPrecedence()
        {
            Assert.Equal(CohortMode.Scan, CohortCommandLine.Parse(new[] { "--cohort-analyze", "--cohort-scan", "--input", "x" }).Mode);
            Assert.Equal(CohortMode.Analyze, CohortCommandLine.Parse(new[] { "--cohort-analyze", "--input", "x" }).Mode);
            Assert.Equal(3, (int)CohortMode.Analyze);
        }
    }
}
