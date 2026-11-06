using System;
using System.IO;
using System.Linq;
using System.Text;
using DicomRtNifti.Core.Analysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The <see cref="ContoursCohort"/>, written once for every test in the class.</summary>
    public sealed class ContoursCohortFixture : IDisposable
    {
        public string Root { get; } = DicomTestData.NewTempDir();
        public string Input { get; }

        public ContoursCohortFixture()
        {
            Input = Path.Combine(Root, "in");
            ContoursCohort.Write(Input);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// --cohort-analyze --tier contours end to end: the document, held byte for byte against a
    /// golden on the cohort that raises every contours-tier code (whose document is the checked-in
    /// example), the six CSV tables, anonymization, and that the tier only adds to the header
    /// tier's document.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortAnalyzeContoursCliTests : IClassFixture<GoldenCohortFixture>, IClassFixture<ContoursCohortFixture>
    {
        private readonly GoldenCohortFixture _golden;
        private readonly ContoursCohortFixture _cohort;

        public CohortAnalyzeContoursCliTests(GoldenCohortFixture golden, ContoursCohortFixture cohort)
        {
            _golden = golden;
            _cohort = cohort;
        }

        [Fact]
        public void ContoursCohort_PrintsTheGoldenAnalysis()
        {
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--tier", "contours");
            Goldens.AssertMatches("cohort-analysis-contours.txt", Goldens.Normalize(run.Stdout, _cohort.Root));
        }

        [Fact]
        public void TheCheckedInContoursExample_IsTheGoldenDocument()
        {
            string example = Path.Combine(RepoRoot(), "examples", "cohort-analysis-contours.example.json");
            string golden = Path.Combine(RepoRoot(), "tests", "DicomRtNifti.Core.Tests", "Goldens", "cohort-analysis-contours.txt");

            Assert.True(File.Exists(example), $"{example} is missing.");
            Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), File.ReadAllText(example).Replace("\r\n", "\n"));
        }

        [Fact]
        public void TheContoursExample_RaisesEveryContoursTierCode()
        {
            var document = JObject.Parse(Run("--cohort-analyze", "--input", _cohort.Input, "--tier", "contours").Stdout);

            var raised = document["flags"].Select(f => (string)f["code"]).Distinct().ToList();
            foreach (var definition in FlagCodes.All.Where(d => d.Tier == AnalysisTierNames.Contours))
                Assert.Contains(definition.Code, raised);
            Assert.Equal("contours", (string)document["tier"]);

            var codes = ((JObject)document["summary"]["flags_by_code"]).Properties().Select(p => p.Name);
            Assert.Equal(FlagCodes.All.Select(d => d.Code), codes);
        }

        /// <summary>The expected counts on the synthesized cohort, the brief's report-back figure.</summary>
        [Fact]
        public void TheContoursExample_RaisesTheExpectedCounts()
        {
            var document = JObject.Parse(Run("--cohort-analyze", "--input", _cohort.Input, "--tier", "contours").Stdout);
            var byCode = (JObject)document["summary"]["flags_by_code"];

            Assert.Equal(1, (int)byCode[FlagCodes.RtUnreadable]);
            Assert.Equal(2, (int)byCode[FlagCodes.RoiEmpty]);
            Assert.Equal(1, (int)byCode[FlagCodes.RoiDegenerate]);
            Assert.Equal(1, (int)byCode[FlagCodes.RoiOffPlane]);
            Assert.Equal(1, (int)byCode[FlagCodes.RoiOutsideFov]);
            Assert.Equal(1, (int)byCode[FlagCodes.DoseNotAligned]);
            // Body and Lost, against each of the two doses.
            Assert.Equal(4, (int)byCode[FlagCodes.DoseNotCoveringRoi]);
        }

        /// <summary>
        /// The header tier's document, at --tier header, carries none of the contours tier's fields
        /// or codes; at --tier contours, removing what the tier adds gives it back exactly.
        /// </summary>
        [Fact]
        public void TheContoursTier_OnlyAddsToTheHeaderTiersDocument()
        {
            foreach (var input in new[] { _golden.Input, _cohort.Input })
            {
                var header = JObject.Parse(Run("--cohort-analyze", "--input", input).Stdout);
                var contours = JObject.Parse(Run("--cohort-analyze", "--input", input, "--tier", "contours").Stdout);

                Assert.DoesNotContain(header.Descendants().OfType<JProperty>(), p => p.Name == "contours" || p.Name == "audit");

                var contourCodes = FlagCodes.All.Where(d => d.Tier == AnalysisTierNames.Contours).Select(d => d.Code).ToList();
                contours["tier"] = header["tier"];
                foreach (var p in contours.Descendants().OfType<JProperty>().Where(p => p.Name == "contours" || p.Name == "audit").ToList())
                    p.Remove();
                foreach (var f in contours["flags"].Where(f => contourCodes.Contains((string)f["code"])).ToList())
                    f.Remove();
                foreach (var s in contours["series"])
                    foreach (var code in s["flags"].Where(c => contourCodes.Contains((string)c)).ToList())
                        code.Remove();
                foreach (var code in contourCodes)
                    ((JObject)contours["summary"]["flags_by_code"]).Remove(code);
                contours["summary"]["flags_by_severity"] = header["summary"]["flags_by_severity"];

                Assert.True(JToken.DeepEquals(header, contours), "The contours tier changed a header-tier field on " + input);
            }
        }

        [Fact]
        public void CsvOut_AtTheContoursTier_WritesSixTables()
        {
            string folder = Path.Combine(_cohort.Root, "csv");
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--tier", "contours", "--csv-out", folder);

            var all = new StringBuilder();
            foreach (var name in new[]
            {
                CohortAnalysisCsv.SeriesFile, CohortAnalysisCsv.RoisFile, CohortAnalysisCsv.RoiNamesFile, CohortAnalysisCsv.FlagsFile,
                CohortAnalysisCsv.RoiContoursFile, CohortAnalysisCsv.DosesFile,
            })
            {
                string path = Path.Combine(folder, name);
                Assert.True(File.Exists(path), $"{name} was not written.\n{run}");
                all.Append("== ").Append(name).Append(" ==\n").Append(File.ReadAllText(path));
            }
            Goldens.AssertMatches("cohort-analysis-contours-csv.txt", Goldens.Normalize(all.ToString(), _cohort.Root));
        }

        /// <summary>No source PatientID, UID, path, file name or reader message naming one.</summary>
        [Fact]
        public void Anonymized_CarriesNoSourceIdentifierOrPath()
        {
            var run = Run("--cohort-analyze", "--input", _cohort.Input, "--tier", "contours", "--anonymize", "--salt", ContoursCohort.Salt);

            foreach (var identifier in new[] { "CN_01", "CN_02", "3.1.1", "3.2.1", ".dcm" })
                Assert.DoesNotContain(identifier, run.Stdout);
            Assert.DoesNotContain(_cohort.Root, run.Stdout);
            Assert.DoesNotContain(_cohort.Root.Replace("\\", "\\\\"), run.Stdout);

            var document = JObject.Parse(run.Stdout);
            Assert.True((bool)document["anonymized"]);
            var unreadable = document["series"].SelectMany(s => s["links"]["rtstructs"]).Single(r => !(bool)r["contours"]["readable"]);
            Assert.Equal(JTokenType.Null, unreadable["contours"]["error"].Type);
        }

        private static CliRun Run(params string[] args)
        {
            var run = CliRun.Execute(args);
            Assert.True(run.ExitCode == 0, run.ToString());
            return run;
        }

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
