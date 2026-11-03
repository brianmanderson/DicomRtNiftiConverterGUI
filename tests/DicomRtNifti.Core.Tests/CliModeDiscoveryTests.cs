using System.Linq;
using DicomRtNifti.Cli.Modes;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// One file per CLI mode, found at startup (D-43). Dispatch order is part of the contract —
    /// the first mode whose flag is present runs, and the single-series modes come first — so it
    /// is pinned here rather than left to reflection order.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CliModeDiscoveryTests
    {
        [Fact]
        public void EveryMode_IsDiscovered_InTheDispatchOrder()
        {
            Assert.Equal(
                new[]
                {
                    "--forward", "--reverse", "--image-reverse", "--image-forward",
                    "--cohort-scan", "--cohort-manifest", "--cohort-convert",
                },
                CliModes.All.Select(m => m.Flag));
            Assert.All(CliModes.All, m =>
            {
                Assert.Equal("DicomRtNifti.Cli.Modes", m.GetType().Namespace);
                Assert.EndsWith("Mode", m.GetType().Name);
                Assert.NotEmpty(m.Usage);
                Assert.Contains(m.Usage, line => line.Contains(m.Flag));
            });
        }

        [Fact]
        public void NoMode_NamesEveryDiscoveredFlag_AndExits2()
        {
            var run = CliRun.Execute("--input", "somewhere");

            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.StartsWith(
                "Headless mode requires --forward, --reverse, --image-reverse, --image-forward, " +
                "--cohort-scan, --cohort-manifest, or --cohort-convert.",
                run.Stderr);
        }

        /// <summary>A single-series flag wins over a cohort flag, whatever order they are given in.</summary>
        [Fact]
        public void ASingleSeriesMode_ShadowsACohortMode()
        {
            var run = CliRun.Execute("--cohort-scan", "--input", "somewhere", "--forward");

            Assert.True(run.ExitCode == 2, run.ToString());
            Assert.Contains("ERROR: Required argument '--rtstruct' is missing.", run.Stderr);
        }
    }
}
