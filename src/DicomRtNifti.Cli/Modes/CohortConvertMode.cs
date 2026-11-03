using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --cohort-convert: export a tree, one folder per series, and print
    /// dicomrtnifti.cohort-convert/1; with --dry-run, print the plan (dicomrtnifti.cohort-plan/1)
    /// and write nothing. The last cohort mode, so its --help block closes the cohort section with
    /// the options the survey and the conversion share.
    /// </summary>
    internal sealed class CohortConvertMode : ICliMode
    {
        public string Flag => CohortCommandLine.ConvertFlag;

        public int Order => 70;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Cohort convert (full export, one folder per series):",
            "  --cohort-convert --input PATH --output PATH",
            "    Layout: <patient>/<seriesDate>_<seriesDescription>/ (two series of a patient",
            "            that would share it each get _<12 hex of their SeriesInstanceUID>), or",
            "            <patient>/<study>/<series>/ (hashes) under --anonymize.",
            "",
        }.Concat(CohortUsage.SharedOptions).ToArray();

        public int Run(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Convert, args, new StderrProgress("  "));
            if (command.DryRun)
                return CohortRunner.RunPlan(command);
            var options = command.Options;
            var (result, plan) = CohortRunner.RunCohort(options, convert: true);

            CohortRunner.Emit(command, CohortConvertDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }
    }
}
