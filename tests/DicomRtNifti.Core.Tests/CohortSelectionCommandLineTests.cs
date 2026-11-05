using System;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The three flags W4-C adds to the cohort grammar: --series, --struct-series and --rois. The
    /// round trip for every option is CohortCommandLineTests'; these pin what is particular to the
    /// selections: how a list is read, that an empty one is refused rather than read as "no
    /// selection", and that the scan, which selects nothing, has no flag for them.
    /// </summary>
    public class CohortSelectionCommandLineTests
    {
        [Theory]
        [InlineData(CohortMode.Manifest)]
        [InlineData(CohortMode.Convert)]
        public void Parse_ReadsTheSelectionLists_TrimmedAndInOrder(CohortMode mode)
        {
            var command = CohortCommandLine.Parse(new[]
            {
                CohortCommandLine.FlagFor(mode), "--input", "in", "--output", "out",
                "--series", " 1.2.3.4 ,1.2.3.5,,", "--struct-series", "1.2.3.9",
                "--rois", "PTV 70, Lung_L,Spinal-Cord",
            });

            Assert.Equal(new[] { "1.2.3.4", "1.2.3.5" }, command.Options.SeriesInstanceUids);
            Assert.Equal(new[] { "1.2.3.9" }, command.Options.StructureSetUids);
            Assert.Equal(new[] { "PTV 70", "Lung_L", "Spinal-Cord" }, command.Options.RoiSelection);
        }

        [Fact]
        public void Parse_WithoutTheFlags_SelectsNothing()
        {
            var options = CohortCommandLine.Parse(new[] { "--cohort-convert", "--input", "in", "--output", "out" }).Options;

            Assert.Empty(options.SeriesInstanceUids);
            Assert.Empty(options.StructureSetUids);
            Assert.Empty(options.RoiSelection);
        }

        /// <summary>
        /// A list flag given with nothing in it would parse to an empty selection, which means no
        /// selection: the run would widen to every series or every ROI. Refused instead.
        /// </summary>
        [Theory]
        [InlineData("--series")]
        [InlineData("--struct-series")]
        [InlineData("--rois")]
        public void Parse_RefusesAListWithNothingInIt(string flag)
        {
            var ex = Assert.Throws<CommandLineUsageException>(() => CohortCommandLine.Parse(new[]
            {
                "--cohort-manifest", "--input", "in", "--output", "out", flag, " , ",
            }));

            Assert.Equal($"{flag}: expected a comma-separated list; got ' , '.", ex.Message);
        }

        [Fact]
        public void Render_PutsTheSelectionsBesideTheFiltersTheyNarrow()
        {
            var options = new CohortExportOptions
            {
                InputRoot = "in",
                OutputRoot = "out",
                SeriesInstanceUids = { "1.2.3.4", "1.2.3.5" },
                StructureSetUids = { "1.2.3.9" },
                RoiSelection = { "Heart" },
                OnlyAssociatedRois = true,
                StructDescriptionFilter = "plan",
            };

            Assert.Equal(new[]
            {
                "--cohort-convert", "--input", "in", "--output", "out",
                "--only-associated-rois", "--rois", "Heart",
                "--series", "1.2.3.4,1.2.3.5",
                "--struct-description", "plan", "--struct-series", "1.2.3.9",
            }, CohortCommandLine.Render(CohortMode.Convert, options, null));
        }

        [Fact]
        public void Render_RefusesWhatACommaSeparatedListCannotCarry()
        {
            var comma = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", RoiSelection = { "Lung, left" } };
            var padded = new CohortExportOptions { InputRoot = "in", OutputRoot = "out", SeriesInstanceUids = { " 1.2.3" } };

            var ex = Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Convert, comma, null));
            Assert.Contains("ROI names", ex.Message);
            Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Manifest, padded, null));
        }

        [Theory]
        [InlineData("series")]
        [InlineData("struct-series")]
        [InlineData("rois")]
        public void Render_OfAScan_RefusesASelection(string which)
        {
            var options = new CohortExportOptions { InputRoot = "in" };
            if (which == "series") options.SeriesInstanceUids.Add("1.2.3");
            if (which == "struct-series") options.StructureSetUids.Add("1.2.3");
            if (which == "rois") options.RoiSelection.Add("Heart");

            var ex = Assert.Throws<ArgumentException>(() => CohortCommandLine.Render(CohortMode.Scan, options, null));
            Assert.StartsWith("--cohort-scan has no flag for", ex.Message);
        }
    }
}
