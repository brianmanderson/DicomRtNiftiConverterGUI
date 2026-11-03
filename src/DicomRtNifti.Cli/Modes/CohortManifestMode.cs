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
    /// --cohort-manifest: survey a tree into the manifest CSV (spacing and per-ROI volume per
    /// series) and print dicomrtnifti.cohort-manifest/1; with --dry-run, print the plan
    /// (dicomrtnifti.cohort-plan/1) and write nothing.
    /// </summary>
    internal sealed class CohortManifestMode : ICliMode
    {
        public string Flag => CohortCommandLine.ManifestFlag;

        public int Order => 60;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Cohort manifest (survey CSV: spacing + per-ROI volume in cc):",
            "  --cohort-manifest --input PATH --output PATH",
            "                       [--no-volumes]         (skip rasterizing; volumes = -1)",
            "                       [--output-spacing X,Y,Z]  (default: report native spacing)",
        };

        public int Run(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Manifest, args, new StderrProgress("  "));
            if (command.DryRun)
                return CohortRunner.RunPlan(command);
            var options = command.Options;
            var (result, plan) = CohortRunner.RunCohort(options, convert: false);

            CohortRunner.Emit(command, CohortManifestDocument.Build(result, CohortDocuments.Identity));

            // --only-associated-rois that matched nothing anywhere is a failed run, reported after
            // the JSON (whose errors list carries the explanation) rather than instead of it.
            if (CohortExportService.DescribeNothingAssociated(plan, options, result) != null)
                return 1;
            return result.FailedCount > 0 && result.SucceededCount == 0 ? 1 : 0;
        }
    }
}
