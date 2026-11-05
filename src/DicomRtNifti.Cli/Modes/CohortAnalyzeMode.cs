using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// --cohort-analyze: scan a tree and print dicomrtnifti.cohort-analysis/1 — linkage, image
    /// geometry, orientation, ROI naming and flags — with no conversion; --csv-out also writes the
    /// analysis' tables. The header tier costs nothing beyond the scan; --tier contours and
    /// --tier raster are refused (exit 2) until they exist. --anonymize hashes identifiers as
    /// --cohort-scan does, reading the key at --key-path and writing none.
    /// </summary>
    internal sealed class CohortAnalyzeMode : ICliMode
    {
        public string Flag => CohortCommandLine.AnalyzeFlag;

        public int Order => 80;

        public IReadOnlyList<string> Usage { get; } = new[]
        {
            "Cohort analyze (linkage, geometry, orientation, ROI naming and flags; no conversion):",
            "  --cohort-analyze --input PATH [--tier header] [--associations FILE.json]",
            "                   [--anonymize [--salt STRING] [--key-path FILE]]",
            "                   [--json-out PATH] [--csv-out FOLDER]",
            "    --tier header costs nothing beyond the scan; contours and raster are not yet",
            "                   available (exit 2)",
            "    --csv-out writes " + CohortAnalysisCsv.SeriesFile + ", " + CohortAnalysisCsv.RoisFile + ",",
            "                   " + CohortAnalysisCsv.RoiNamesFile + " and " + CohortAnalysisCsv.FlagsFile,
        };

        public int Run(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Analyze, args, new StderrProgress("  "));
            var options = command.Options;
            string input = options.InputRoot;
            if (!Directory.Exists(input))
                throw new CliUsageException($"Input folder not found: {input}");

            // As --cohort-scan: opened before the scan so a damaged key or a disagreeing salt
            // refuses the run without waiting out a tree walk; only --key-path is read.
            AnonymizationService anon = null;
            if (options.Anonymize)
            {
                anon = ReadOnlyAnonymizationKey.OpenFile(options.KeyPath, options.Salt);
                Console.Error.WriteLine(anon.KeyFilePath == null
                    ? "  Anonymized with the salt alone (no --key-path; no key read or written)"
                    : File.Exists(anon.KeyFilePath)
                        ? $"  Anonymization key: {anon.KeyFilePath} (read only)"
                        : $"  Anonymization key: {anon.KeyFilePath} not found; hashes from the salt alone");
            }

            var scan = CohortRunner.ScanTree(input);

            var analysis = CohortAnalysisService.Analyze(scan, new CohortAnalysisOptions
            {
                Tier = command.Tier,
                InputRoot = input,
                Associations = options.Associations,
                AssociationsName = command.AssociationsPath == null ? null : Path.GetFileName(command.AssociationsPath),
            }, anon);
            var severities = analysis.Summary.FlagsBySeverity;
            Console.Error.WriteLine(
                $"  Analyzed {analysis.Summary.ImageSeries} image series: {analysis.Flags.Count} flag(s) " +
                $"({severities[FlagSeverity.Serious]} serious, {severities[FlagSeverity.Warning]} warning, {severities[FlagSeverity.Info]} info)");

            CohortRunner.Emit(command, CohortAnalysisDocument.Build(analysis, CohortDocuments.Identity));

            if (!string.IsNullOrEmpty(command.CsvOutPath))
            {
                Directory.CreateDirectory(command.CsvOutPath);
                foreach (var table in CohortAnalysisCsv.Build(analysis))
                {
                    string path = Path.Combine(command.CsvOutPath, table.Key);
                    File.WriteAllText(path, table.Value);
                    Console.Error.WriteLine($"  Wrote {path}");
                }
            }
            return 0;
        }
    }
}
