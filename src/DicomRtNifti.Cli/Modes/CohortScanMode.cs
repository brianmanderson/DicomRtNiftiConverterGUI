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
    /// --cohort-scan: the inventory of a DICOM tree as dicomrtnifti.cohort-scan/1 on stdout; no
    /// conversion. --anonymize hashes its identifiers, reading the key at --key-path and writing
    /// none.
    /// </summary>
    internal sealed class CohortScanMode : ICliMode
    {
        public string Flag => CohortCommandLine.ScanFlag;

        public int Order => 50;

        public IReadOnlyList<string> Usage { get; } = CohortUsage.Preamble.Concat(new[]
        {
            "",
            "Cohort scan (inventory only; no conversion):",
            "  --cohort-scan --input PATH [--json-out PATH]",
        }).ToArray();

        public int Run(string[] args)
        {
            var command = CohortCommandLine.Parse(CohortMode.Scan, args, new StderrProgress("  "));
            var options = command.Options;
            string input = options.InputRoot;
            if (!Directory.Exists(input))
                throw new CliUsageException($"Input folder not found: {input}");

            // Opened before the scan for the same reason as the export's key: a damaged key or a
            // salt it disagrees with refuses the run, and should not wait out a full tree walk.
            // There is no output folder to place a key beside, so only --key-path is read.
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

            CohortRunner.Emit(command, CohortScanDocument.Build(scan, input, CohortDocuments.Identity, anon));
            return 0;
        }
    }
}
