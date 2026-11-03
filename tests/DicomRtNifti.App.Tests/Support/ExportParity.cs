using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Core.Tests;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// The forward window's toggles for one parity case, and the <see cref="CohortExportOptions"/>
    /// they stand for. The mapping is the one W3-D.2 has to implement when Convert Selected moves
    /// onto the service: every toggle the GUI reads in ExecuteConvertAsync, and nothing else (no
    /// patient filter, no description filters, no Require*, no largest-series rule).
    /// </summary>
    public sealed class ExportToggles
    {
        public bool ExportImages { get; set; } = true;
        public bool IncludeStructures { get; set; } = true;
        public bool IncludeDose { get; set; } = true;
        public bool Anonymize { get; set; }

        /// <summary>Output spacing [x, y, z] in mm, or null for each series' native grid.</summary>
        public double[] OutputSpacing { get; set; }

        /// <summary>The ROI associations the GUI loads from roi_associations.json.</summary>
        public List<RoiAssociation> Associations { get; set; } = new List<RoiAssociation>();

        public string Salt { get; set; } = AnonymizationService.DefaultSalt;

        public CohortExportOptions ToServiceOptions(string inputRoot, string outputRoot) => new CohortExportOptions
        {
            InputRoot = inputRoot,
            OutputRoot = outputRoot,
            ExportImages = ExportImages,
            ExportStructures = IncludeStructures,
            ExportDoses = IncludeDose,
            Associations = Associations,
            // "Only export specific ROIs" is off in every case: with no ROI selection made it is
            // OnlyAssociatedRois, and the selection itself has no Core equivalent until W4-C.
            OnlyAssociatedRois = false,
            OutputSpacing = OutputSpacing == null ? null : (double[])OutputSpacing.Clone(),
            Anonymize = Anonymize,
            Salt = Salt,
        };
    }

    /// <summary>What one export left behind, read back into memory.</summary>
    public sealed class ExportSnapshot
    {
        /// <summary>
        /// Every directory (trailing '/') and file under the output root except the manifest,
        /// relative with forward slashes, mapped to its SHA-256 and length.
        /// </summary>
        public SortedDictionary<string, string> Tree { get; set; }

        /// <summary>export_manifest.csv, byte for byte; null when it was not written.</summary>
        public byte[] Manifest { get; set; }

        /// <summary>AnonymizationKey.json beside the export, byte for byte; null when there is none.</summary>
        public byte[] Key { get; set; }

        /// <summary>What the run reported, for the test log.</summary>
        public string Log { get; set; }

        /// <summary>
        /// One SHA-256 over the whole export: every <see cref="Tree"/> line, the manifest and the
        /// key. The "parity hash" W3-D.2 reports before and after the migration; the inputs use
        /// fixed UIDs, so it is the same on every run on one platform.
        /// </summary>
        public string Hash
        {
            get
            {
                var text = new StringBuilder();
                foreach (var entry in Tree)
                    text.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');
                text.Append("manifest\t").Append(Manifest == null ? "none" : ExportParity.Sha256(Manifest)).Append('\n');
                text.Append("key\t").Append(Key == null ? "none" : ExportParity.Sha256(Key)).Append('\n');
                return ExportParity.Sha256(Encoding.UTF8.GetBytes(text.ToString()));
            }
        }
    }

    /// <summary>The GUI's and the service's export of one input.</summary>
    public sealed class ParityRun
    {
        public ExportSnapshot Gui { get; set; }
        public ExportSnapshot Service { get; set; }
    }

    /// <summary>
    /// Runs one synthesized cohort through both forward paths and snapshots what each wrote:
    ///
    /// 1. the GUI: DICOM → NIfTI opened from the launcher, scanned, Convert Selected over every
    ///    series, driven as the bound controls drive it (the same route WorkflowRoundTripTests and
    ///    AnonymizedExportMetadataTests take);
    /// 2. the service: a fresh scan of the same folder, <see cref="CohortExportService.BuildPlan"/>
    ///    and <see cref="CohortExportService.ExecuteAsync"/>, with the key opened through
    ///    <see cref="AnonymizationKeyLocation.Open"/> as the CLI's cohort modes open it.
    ///
    /// Each path writes to its own output root under one temp folder, so the two keys land in two
    /// sibling .key folders and every relative path can be compared. The temp folder is deleted
    /// once both snapshots are in memory.
    /// </summary>
    public static class ExportParity
    {
        // One run per case serves every aspect a test compares (files, manifest, key), so a case
        // converts twice rather than twice per aspect. The App tests never run in parallel.
        private static readonly Dictionary<string, Task<ParityRun>> Runs = new Dictionary<string, Task<ParityRun>>();

        public static Task<ParityRun> RunOnceAsync(string caseName, Action<string> writeInput, ExportToggles toggles)
        {
            if (!Runs.TryGetValue(caseName, out var run))
            {
                run = RunAsync(writeInput, toggles);
                Runs[caseName] = run;
            }
            return run;
        }

        private static async Task<ParityRun> RunAsync(Action<string> writeInput, ExportToggles toggles)
        {
            string root = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", "parity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string input = Path.Combine(root, "dicom");
                Directory.CreateDirectory(input);
                writeInput(input);

                string guiOut = Path.Combine(root, "gui", "out");
                string serviceOut = Path.Combine(root, "service", "out");
                Directory.CreateDirectory(guiOut);
                Directory.CreateDirectory(serviceOut);

                string guiLog = await RunGuiAsync(input, guiOut, toggles);
                string serviceLog = await RunServiceAsync(input, serviceOut, toggles);

                return new ParityRun
                {
                    Gui = Snapshot(guiOut, toggles.Anonymize, guiLog),
                    Service = Snapshot(serviceOut, toggles.Anonymize, serviceLog),
                };
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* a straggling handle on Windows; the temp folder is per-run anyway */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task<string> RunGuiAsync(string input, string output, ExportToggles toggles)
        {
            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                launcher.Services.Settings.SaveSettings(new AppSettings { HashSalt = toggles.Salt });
                launcher.Services.Settings.SaveAssociations(toggles.Associations);

                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                try
                {
                    // Every toggle is set explicitly so a stored preference can never change the tree.
                    vm.InputFolder = input;
                    vm.OutputFolder = output;
                    vm.ExportImages = toggles.ExportImages;
                    vm.IncludeStructures = toggles.IncludeStructures;
                    vm.IncludeDose = toggles.IncludeDose;
                    vm.OnlyExportSpecificRois = false;
                    vm.SpecifyOutputSpacing = toggles.OutputSpacing != null;
                    if (toggles.OutputSpacing != null)
                    {
                        vm.OutputSpacingX = toggles.OutputSpacing[0];
                        vm.OutputSpacingY = toggles.OutputSpacing[1];
                        vm.OutputSpacingZ = toggles.OutputSpacing[2];
                    }
                    vm.AnonymizeExport = toggles.Anonymize;
                    vm.AnonymizationKeyPath = "";
                    vm.ExportDicomMetadata = false;

                    await vm.ScanCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("Scan complete.", vm.StatusText);

                    vm.AllPatientsSelected = true;
                    int logStart = vm.LogText.Length;
                    Assert.True(vm.ConvertSelectedCommand.CanExecute(null));
                    await vm.ConvertSelectedCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();

                    string log = vm.LogText.Substring(logStart);
                    Assert.DoesNotContain("Conversion error", log);
                    Assert.DoesNotContain("FAILED", log);
                    Assert.Contains("Conversion complete.", log);
                    return log;
                }
                finally
                {
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }

        private static async Task<string> RunServiceAsync(string input, string output, ExportToggles toggles)
        {
            var options = toggles.ToServiceOptions(input, output);

            AnonymizationService anon = null;
            if (options.Anonymize)
                anon = AnonymizationKeyLocation.Open(options.OutputRoot, options.KeyPath, options.Salt, out _);

            var scan = await new DicomScannerService().ScanFolderAsync(input, null, CancellationToken.None);
            var plan = CohortExportService.BuildPlan(scan, options, anon);

            var progress = new CollectingProgress();
            var service = new CohortExportService(new NiftiConversionService(new RtStructMaskService()));
            var result = await service.ExecuteAsync(plan, options, anon, progress, CancellationToken.None);

            Assert.Equal(0, result.FailedCount);
            Assert.Empty(result.Errors);
            Assert.Empty(plan.Skipped);
            Assert.Empty(plan.UnlinkedRtObjects);

            var log = new StringBuilder();
            log.AppendLine($"planned {plan.Series.Count} series:");
            foreach (var planned in plan.Series)
                log.AppendLine("  " + planned.RelativeOutputDir);
            foreach (var message in progress.Messages)
                log.AppendLine(message);
            return log.ToString();
        }

        private static ExportSnapshot Snapshot(string outputRoot, bool anonymized, string log)
        {
            string manifestPath = Path.Combine(outputRoot, ExportManifestService.DefaultFileName);
            var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (string dir in Directory.GetDirectories(outputRoot, "*", SearchOption.AllDirectories))
                tree[Relative(outputRoot, dir) + "/"] = "directory";

            foreach (string file in Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(manifestPath), StringComparison.Ordinal))
                    continue;
                byte[] bytes = File.ReadAllBytes(file);
                tree[Relative(outputRoot, file)] = $"sha256 {Sha256(bytes)}, {bytes.Length} B";
            }

            string keyPath = AnonymizationKeyLocation.DefaultFor(outputRoot);
            return new ExportSnapshot
            {
                Tree = tree,
                Manifest = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null,
                Key = anonymized && File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : null,
                Log = log,
            };
        }

        /// <summary>
        /// One line per path that is only on one side or whose bytes differ, ordinal by path;
        /// empty when the two trees are the same.
        /// </summary>
        public static List<string> DiffTrees(IReadOnlyDictionary<string, string> gui, IReadOnlyDictionary<string, string> service)
        {
            var lines = new List<string>();
            foreach (string path in gui.Keys.Union(service.Keys).OrderBy(p => p, StringComparer.Ordinal))
            {
                bool inGui = gui.TryGetValue(path, out string g);
                bool inService = service.TryGetValue(path, out string s);
                if (!inService)
                    lines.Add($"only in the GUI's export:     {path}");
                else if (!inGui)
                    lines.Add($"only in the service's export: {path}");
                else if (g != s)
                    lines.Add($"different bytes:              {path} (GUI {g}; service {s})");
            }
            return lines;
        }

        /// <summary>
        /// A manifest with its ROI columns put in ordinal order, rows kept in file order: the same
        /// cells, read by column name rather than by position. Lets a test pin every spacing and
        /// volume while the byte-for-byte comparison waits on a column-order finding.
        /// </summary>
        public static string ByColumnName(byte[] csv)
        {
            var lines = Encoding.UTF8.GetString(csv)
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
            var header = ExportManifestService.ParseCsvLine(lines[0]);
            const int fixedColumns = 6;
            var order = Enumerable.Range(0, fixedColumns)
                .Concat(Enumerable.Range(fixedColumns, header.Count - fixedColumns)
                    .OrderBy(i => header[i], StringComparer.Ordinal))
                .ToList();

            var text = new StringBuilder();
            text.AppendLine(string.Join(" | ", order.Select(i => header[i])));
            foreach (string line in lines.Skip(1))
            {
                var cells = ExportManifestService.ParseCsvLine(line);
                text.AppendLine(string.Join(" | ", order.Select(i => i < cells.Count ? cells[i] : "<missing>")));
            }
            return text.ToString();
        }

        public static string Describe(IReadOnlyDictionary<string, string> tree) =>
            string.Join("\n", tree.Select(kv => $"  {kv.Key}  {(kv.Value == "directory" ? "" : kv.Value)}"));

        private static string Relative(string root, string path) =>
            Path.GetFullPath(path).Substring(Path.GetFullPath(root).Length + 1).Replace('\\', '/');

        internal static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }
    }
}
