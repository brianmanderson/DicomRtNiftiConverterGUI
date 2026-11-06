using System;
using System.IO;
using System.Linq;
using System.Text;
using DicomRtNifti.Core.Analysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The <see cref="AnalysisCohort"/> and its association file, written once for every test in the class.</summary>
    public sealed class AnalysisCohortFixture : IDisposable
    {
        public string Root { get; } = DicomTestData.NewTempDir();
        public string Input { get; }
        public string AssociationsPath { get; }

        public AnalysisCohortFixture()
        {
            Input = Path.Combine(Root, "in");
            AnalysisCohort.Write(Input);
            AssociationsPath = AnalysisCohort.WriteAssociations(Path.Combine(Root, "associations.json"));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// --cohort-analyze end to end: the document it prints, held byte for byte against goldens on
    /// the cohort the other cohort documents are pinned on and on the cohort that raises every flag
    /// (whose document is the checked-in example), the CSV tables, and the refusals.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortAnalyzeCliTests : IClassFixture<GoldenCohortFixture>, IClassFixture<AnalysisCohortFixture>
    {
        private readonly GoldenCohortFixture _golden;
        private readonly AnalysisCohortFixture _cohort;

        public CohortAnalyzeCliTests(GoldenCohortFixture golden, AnalysisCohortFixture cohort)
        {
            _golden = golden;
            _cohort = cohort;
        }

        [Fact]
        public void GoldenCohort_PrintsTheGoldenAnalysis()
        {
            var run = Run("--cohort-analyze", "--input", _golden.Input);
            Goldens.AssertMatches("cohort-analysis.txt", Goldens.Normalize(run.Stdout, _golden.Root));
        }

        [Fact]
        public void GoldenCohortAnonymized_PrintsTheGoldenAnalysis()
        {
            var run = Run("--cohort-analyze", "--input", _golden.Input, "--anonymize", "--salt", GoldenCohort.Salt);
            Goldens.AssertMatches("cohort-analysis-anonymized.txt", Goldens.Normalize(run.Stdout, _golden.Root));
        }

        /// <summary>The example document: every flag code, an association set, the ROI naming tables full.</summary>
        [Fact]
        public void AnalysisCohort_PrintsTheGoldenAnalysis()
        {
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--associations", _cohort.AssociationsPath);
            Goldens.AssertMatches("cohort-analysis-flags.txt", Goldens.Normalize(run.Stdout, _cohort.Root));
        }

        [Fact]
        public void TheCheckedInExample_IsTheGoldenDocument()
        {
            string example = Path.Combine(RepoRoot(), "examples", "cohort-analysis.example.json");
            string golden = Path.Combine(RepoRoot(), "tests", "DicomRtNifti.Core.Tests", "Goldens", "cohort-analysis-flags.txt");

            Assert.True(File.Exists(example), $"{example} is missing.");
            Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), File.ReadAllText(example).Replace("\r\n", "\n"));
        }

        /// <summary>A code is a published contract: README's table names each one with its severity.</summary>
        [Fact]
        public void TheReadme_ListsEveryFlagCode_WithItsSeverity()
        {
            string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

            foreach (var definition in FlagCodes.All)
                Assert.Contains($"| `{definition.Code}` | {definition.Severity} |", readme);
        }

        /// <summary>Every header-tier code; the contours tier's have an example of their own (CohortAnalyzeContoursCliTests).</summary>
        [Fact]
        public void TheExample_RaisesEveryHeaderTierFlagCode()
        {
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--associations", _cohort.AssociationsPath);
            var document = JObject.Parse(run.Stdout);

            var raised = document["flags"].Select(f => (string)f["code"]).Distinct().OrderBy(c => c).ToList();
            Assert.Equal(FlagCodes.All.Where(d => d.Tier == AnalysisTierNames.Header).Select(d => d.Code).OrderBy(c => c), raised);
            Assert.Equal(CohortAnalysisDocument.Schema, (string)document["schema"]);
            Assert.Equal("header", (string)document["tier"]);
        }

        [Fact]
        public void CsvOut_WritesTheFourTables()
        {
            string folder = Path.Combine(_cohort.Root, "csv");
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--associations", _cohort.AssociationsPath,
                "--csv-out", folder);

            var all = new StringBuilder();
            foreach (var name in new[] { CohortAnalysisCsv.SeriesFile, CohortAnalysisCsv.RoisFile, CohortAnalysisCsv.RoiNamesFile, CohortAnalysisCsv.FlagsFile })
            {
                string path = Path.Combine(folder, name);
                Assert.True(File.Exists(path), $"{name} was not written.\n{run}");
                all.Append("== ").Append(name).Append(" ==\n").Append(File.ReadAllText(path));
            }
            Goldens.AssertMatches("cohort-analysis-flags-csv.txt", Goldens.Normalize(all.ToString(), _cohort.Root));
        }

        [Fact]
        public void JsonOut_WritesTheDocumentPrinted()
        {
            string path = Path.Combine(_cohort.Root, "out", "analysis.json");
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--json-out", path);

            Assert.Equal(run.Stdout.TrimEnd(), File.ReadAllText(path).TrimEnd());
        }

        [Theory]
        [InlineData("raster")]
        [InlineData("RASTER")]
        public void TheRasterTier_IsRefusedWithExit2(string tier)
        {
            var run = CliRun.Execute("--cohort-analyze", "--input", _cohort.Input, "--tier", tier);

            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Equal("", run.Stdout);
            Assert.Contains($"ERROR: --tier {tier.ToLowerInvariant()} is not yet available; only --tier header and --tier contours are.", run.Stderr);
            Assert.DoesNotContain("Scanning", run.Stderr);
        }

        [Fact]
        public void AnUnknownTier_AndAMissingInput_AreRefusedWithExit2()
        {
            var unknown = CliRun.Execute("--cohort-analyze", "--input", _cohort.Input, "--tier", "pixels");
            Assert.True(unknown.ExitCode == 2, unknown.ToString());
            Assert.Contains("ERROR: --tier: expected header, contours or raster; got 'pixels'.", unknown.Stderr);

            var missing = CliRun.Execute("--cohort-analyze", "--input", Path.Combine(_cohort.Root, "nope"));
            Assert.True(missing.ExitCode == 2, missing.ToString());
            Assert.Contains("ERROR: Input folder not found:", missing.Stderr);
        }

        [Fact]
        public void TheHeaderTier_IsTheDefault_AndCanBeNamed()
        {
            var named = Run("--cohort-analyze", "--input", _golden.Input, "--tier", "HEADER");
            var implied = Run("--cohort-analyze", "--input", _golden.Input);

            Assert.Equal(implied.Stdout, named.Stdout);
        }

        /// <summary>No source PatientID, UID, path or file name; the hashes are the export's.</summary>
        [Fact]
        public void Anonymized_CarriesNoSourceIdentifierOrPath()
        {
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--anonymize", "--salt", AnalysisCohort.Salt,
                "--associations", _cohort.AssociationsPath);

            foreach (var identifier in new[] { "AN_01", "AN_02", "AN_03", "2.1.1", "2.2.1", "2.3.1", "2.3.2", "ct_x.dcm", ".dcm" })
                Assert.DoesNotContain(identifier, run.Stdout);
            Assert.DoesNotContain(_cohort.Root, run.Stdout);
            Assert.DoesNotContain(_cohort.Root.Replace("\\", "\\\\"), run.Stdout);

            var document = JObject.Parse(run.Stdout);
            Assert.True((bool)document["anonymized"]);
            Assert.Equal(JTokenType.Null, document["input_root"].Type);
            string hash = new Services.AnonymizationService(null, AnalysisCohort.Salt).GetPatientHash("AN_01");
            Assert.Equal(hash, (string)document["series"][0]["patient_id"]);
        }

        private static CliRun Run(params string[] args)
        {
            var run = CliRun.Execute(args);
            Assert.True(run.ExitCode == 0, run.ToString());
            return run;
        }

        /// <summary>The toolkit's root, found by walking up from the test binaries to the solution.</summary>
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DicomRtNifti.sln")))
                    return dir.FullName;
            }
            throw new DirectoryNotFoundException("DicomRtNifti.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
