using System;
using System.IO;
using System.Reflection;
using DicomRtNifti.Cli;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The <see cref="GoldenCohort"/>, written once for every test in the class.</summary>
    public sealed class GoldenCohortFixture : IDisposable
    {
        public string Root { get; } = DicomTestData.NewTempDir();
        public string Input { get; }

        public GoldenCohortFixture()
        {
            Input = Path.Combine(Root, "in");
            GoldenCohort.Write(Input);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// What the cohort modes print on stdout, and the usage text, held byte for byte against
    /// files recorded before their builders moved out of the CLI. These documents are the
    /// contract scripts and notebooks parse, and the usage text is what a user reads; a refactor
    /// that changes either has changed the tool.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CohortCliGoldenTests : IClassFixture<GoldenCohortFixture>
    {
        private readonly GoldenCohortFixture _cohort;

        public CohortCliGoldenTests(GoldenCohortFixture cohort)
        {
            _cohort = cohort;
        }

        [Fact]
        public void CohortScan_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-scan.txt", "--cohort-scan", "--input", _cohort.Input);
        }

        /// <summary>New in W4-A, recorded with it: hashes only, and nothing that has no hash.</summary>
        [Fact]
        public void CohortScanAnonymized_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-scan-anonymized.txt",
                "--cohort-scan", "--input", _cohort.Input, "--anonymize", "--salt", GoldenCohort.Salt);
        }

        [Fact]
        public void CohortManifest_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-manifest.txt",
                "--cohort-manifest", "--input", _cohort.Input, "--output", Output("manifest"));
        }

        /// <summary>The skipped list, volumes not computed, and a patient filter.</summary>
        [Fact]
        public void CohortManifestFiltered_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-manifest-filtered.txt",
                "--cohort-manifest", "--input", _cohort.Input, "--output", Output("manifest-filtered"),
                "--require-structures", "--no-volumes", "--patients", "PAT_A,PAT_B");
        }

        [Fact]
        public void CohortConvert_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-convert.txt",
                "--cohort-convert", "--input", _cohort.Input, "--output", Output("convert"));
        }

        [Fact]
        public void CohortConvertAnonymized_PrintsTheGoldenDocument()
        {
            AssertDocument("cohort-convert-anonymized.txt",
                "--cohort-convert", "--input", _cohort.Input, "--output", Output("convert-anon"),
                "--anonymize", "--salt", GoldenCohort.Salt);
        }

        // The plan document (dicomrtnifti.cohort-plan/1) is new in W4-A; its goldens were recorded
        // with it, so a later change to the plan's shape is as visible as one to the older three.

        [Fact]
        public void CohortConvertDryRun_PrintsTheGoldenPlan()
        {
            AssertDocument("cohort-plan-convert.txt",
                "--cohort-convert", "--input", _cohort.Input, "--output", Output("plan-convert"), "--dry-run");
            Assert.False(Directory.Exists(Output("plan-convert")));
        }

        [Fact]
        public void CohortConvertDryRunAnonymized_PrintsTheGoldenPlan()
        {
            AssertDocument("cohort-plan-convert-anonymized.txt",
                "--cohort-convert", "--input", _cohort.Input, "--output", Output("plan-convert-anon"),
                "--anonymize", "--salt", GoldenCohort.Salt, "--dry-run");
            Assert.False(Directory.Exists(Output("plan-convert-anon")));
            Assert.False(Directory.Exists(Output("plan-convert-anon") + ".key"));
        }

        /// <summary>Skipped series, a resample target (which rewords the spacing warning), a patient filter.</summary>
        [Fact]
        public void CohortManifestDryRunFiltered_PrintsTheGoldenPlan()
        {
            AssertDocument("cohort-plan-manifest-filtered.txt",
                "--cohort-manifest", "--input", _cohort.Input, "--output", Output("plan-manifest"),
                "--require-structures", "--patients", "PAT_A,PAT_B,PAT_C", "--output-spacing", "1,1,2", "--dry-run");
            Assert.False(Directory.Exists(Output("plan-manifest")));
        }

        [Fact]
        public void Help_PrintsTheGoldenUsage()
        {
            var run = CliRun.Execute("--help");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal("", run.Stdout);
            string usage = run.Stderr.Replace(VersionLine(), "DicomRtNifti CLI <VERSION>");
            Goldens.AssertMatches("help.txt", Goldens.Normalize(usage, _cohort.Root));
        }

        private void AssertDocument(string golden, params string[] args)
        {
            var run = CliRun.Execute(args);

            Assert.True(run.ExitCode == 0, run.ToString());
            Goldens.AssertMatches(golden, Goldens.Normalize(run.Stdout, _cohort.Root));
        }

        /// <summary>A fresh output folder per test, so no run merges into another's manifest.</summary>
        private string Output(string name) => Path.Combine(_cohort.Root, name);

        private static string VersionLine()
        {
            var asm = typeof(HeadlessRunner).Assembly;
            string ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
            return "DicomRtNifti CLI " + ver;
        }
    }
}
