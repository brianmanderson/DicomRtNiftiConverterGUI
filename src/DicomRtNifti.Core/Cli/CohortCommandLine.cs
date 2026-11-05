using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

// The folder is Cli/, the path the plan's contention table and later briefs name; the namespace is
// CommandLine because a DicomRtNifti.Core.Cli namespace would shadow DicomRtNifti.Cli for every
// type inside DicomRtNifti.Core.*, where `Cli.HeadlessRunner` would then stop resolving.
namespace DicomRtNifti.Core.CommandLine
{
    /// <summary>
    /// The cohort modes, in the order a command line that names several picks them. New modes
    /// are appended, so the values the earlier ones carry never change.
    /// </summary>
    public enum CohortMode
    {
        Scan,
        Manifest,
        Convert,
        Analyze,
    }

    /// <summary>
    /// The command line cannot be acted on: a required flag is missing or malformed, or a file it
    /// names does not exist. Raised only by argument checks — this grammar's and the CLI's own —
    /// never from inside a conversion, which is what lets the CLI map exactly this type to exit 2
    /// and keep exit 1, with its stack trace, for every failure of the work itself.
    /// </summary>
    public class CommandLineUsageException : Exception
    {
        public CommandLineUsageException(string message) : base(message) { }
    }

    /// <summary>A cohort command line, parsed: the mode, its options, and what only the command line carries.</summary>
    public sealed class CohortCommand
    {
        public CohortMode Mode { get; set; }

        public CohortExportOptions Options { get; set; } = new CohortExportOptions();

        /// <summary>
        /// The file <see cref="CohortExportOptions.Associations"/> was read from, or null when none
        /// was given. Kept so the command can be rendered back: the options hold the list, not
        /// where it came from.
        /// </summary>
        public string AssociationsPath { get; set; }

        /// <summary>--json-out: also write the document to this file. Null when not given.</summary>
        public string JsonOutPath { get; set; }

        /// <summary>
        /// --dry-run on --cohort-manifest or --cohort-convert: scan and plan, print the plan
        /// (<see cref="Documents.CohortPlanDocument"/>), and write nothing.
        /// </summary>
        public bool DryRun { get; set; }

        /// <summary>--tier on --cohort-analyze: how much the analysis reads. Header unless given.</summary>
        public AnalysisTier Tier { get; set; } = AnalysisTier.Header;

        /// <summary>--csv-out on --cohort-analyze: also write the analysis' tables into this folder. Null when not given.</summary>
        public string CsvOutPath { get; set; }
    }

    /// <summary>
    /// Exact-string, case-insensitive argv matching with no subcommand grammar: a mode is just a
    /// flag that happens to be present. That is why every mode name has to stay distinct —
    /// nothing prevents two modes being passed at once; the first check simply wins.
    /// </summary>
    public static class CommandLineArgs
    {
        /// <summary>True when <paramref name="flag"/> appears anywhere in <paramref name="args"/>.</summary>
        public static bool HasFlag(string[] args, string flag) =>
            args != null && args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        /// <summary>The value following <paramref name="name"/>, or a usage error when it is absent.</summary>
        public static string RequireArg(string[] args, string name)
        {
            string value = OptionalArg(args, name);
            if (value == null)
                throw new CommandLineUsageException($"Required argument '{name}' is missing.");
            return value;
        }

        /// <summary>
        /// The value following <paramref name="name"/>, or null when the flag is absent or is the
        /// final token (so has no value after it).
        /// </summary>
        public static string OptionalArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>The value following the first of <paramref name="names"/> that is present.</summary>
        public static string OptionalArgAny(string[] args, params string[] names)
        {
            foreach (var name in names)
            {
                string value = OptionalArg(args, name);
                if (value != null) return value;
            }
            return null;
        }
    }

    /// <summary>
    /// The cohort modes' command-line grammar, owned by Core so every front-end speaks it the same
    /// way: the CLI parses argv with it, and a front-end that builds options in a form can render
    /// the command that reproduces them (<see cref="Render(CohortCommand, PathPresenter)"/>).
    /// Render then parse gives back equal options for every field a mode carries; the round-trip
    /// test enumerates <see cref="CohortExportOptions"/>, so an option added there without a flag
    /// here fails it.
    /// </summary>
    public static class CohortCommandLine
    {
        public const string ScanFlag = "--cohort-scan";
        public const string ManifestFlag = "--cohort-manifest";
        public const string ConvertFlag = "--cohort-convert";
        public const string AnalyzeFlag = "--cohort-analyze";
        public const string DryRunFlag = "--dry-run";

        /// <summary><see cref="CohortCommand.Tier"/>: header, contours or raster (only header is available yet).</summary>
        public const string TierFlag = "--tier";

        /// <summary><see cref="CohortCommand.CsvOutPath"/>: the folder the analysis' tables are written into.</summary>
        public const string CsvOutFlag = "--csv-out";

        /// <summary><see cref="CohortExportOptions.SeriesInstanceUids"/>: export only these image series.</summary>
        public const string SeriesFlag = "--series";

        /// <summary><see cref="CohortExportOptions.StructureSetUids"/>: the structure set to export where a series has several.</summary>
        public const string StructSeriesFlag = "--struct-series";

        /// <summary><see cref="CohortExportOptions.RoiSelection"/>: export only the ROIs these canonical names cover.</summary>
        public const string RoisFlag = "--rois";

        private static readonly CohortExportOptions Defaults = new CohortExportOptions();

        public static string FlagFor(CohortMode mode)
        {
            switch (mode)
            {
                case CohortMode.Scan: return ScanFlag;
                case CohortMode.Manifest: return ManifestFlag;
                case CohortMode.Convert: return ConvertFlag;
                case CohortMode.Analyze: return AnalyzeFlag;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        // -----------------------------------------------------------------------------------
        //  Parse
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Parses a cohort command line whose mode is one of its flags, taking scan, then manifest,
        /// then convert, then analyze when several are present.
        /// </summary>
        /// <exception cref="CommandLineUsageException">No cohort mode, or see the other overload.</exception>
        public static CohortCommand Parse(string[] args, IProgress<string> log = null)
        {
            foreach (CohortMode mode in Enum.GetValues(typeof(CohortMode)))
            {
                if (CommandLineArgs.HasFlag(args, FlagFor(mode)))
                    return Parse(mode, args, log);
            }
            throw new CommandLineUsageException(
                $"A cohort command line needs {ScanFlag}, {ManifestFlag}, {ConvertFlag}, or {AnalyzeFlag}.");
        }

        /// <summary>
        /// Parses the options of <paramref name="mode"/>. Checks run in a fixed order, so the first
        /// problem reported is always the same one. Reads the associations file when one is given
        /// (and says so on <paramref name="log"/>); does not look at the input or output folders.
        /// </summary>
        /// <exception cref="CommandLineUsageException">
        /// A required flag is missing, a value is malformed, a metadata keyword is unrecognised, or
        /// the associations file does not exist.
        /// </exception>
        public static CohortCommand Parse(CohortMode mode, string[] args, IProgress<string> log = null)
        {
            var command = new CohortCommand { Mode = mode };

            if (mode == CohortMode.Scan)
            {
                // The inventory converts nothing, so of the export options it takes only the
                // anonymization ones: --anonymize hashes its identifiers as the other documents do,
                // with the key at --key-path read, never written.
                command.Options = new CohortExportOptions
                {
                    InputRoot = CommandLineArgs.RequireArg(args, "--input"),
                    Anonymize = CommandLineArgs.HasFlag(args, "--anonymize"),
                };
                ParseAnonymization(args, command.Options);
                command.JsonOutPath = CommandLineArgs.OptionalArg(args, "--json-out");
                return command;
            }

            if (mode == CohortMode.Analyze)
            {
                // The analysis converts nothing either: it takes the input, the anonymization
                // options (the key at --key-path read, never written, as --cohort-scan does), and
                // the associations its ROI naming resolves against.
                command.Options = new CohortExportOptions
                {
                    InputRoot = CommandLineArgs.RequireArg(args, "--input"),
                    Anonymize = CommandLineArgs.HasFlag(args, "--anonymize"),
                };
                command.Tier = ParseTier(args);
                ParseAnonymization(args, command.Options);
                ParseAssociations(args, command, log);
                command.JsonOutPath = CommandLineArgs.OptionalArg(args, "--json-out");
                command.CsvOutPath = CommandLineArgs.OptionalArg(args, CsvOutFlag);
                return command;
            }

            var options = new CohortExportOptions
            {
                InputRoot = CommandLineArgs.RequireArg(args, "--input"),
                OutputRoot = CommandLineArgs.RequireArg(args, "--output"),
                OnlyAssociatedRois = CommandLineArgs.HasFlag(args, "--only-associated-rois"),
                Anonymize = CommandLineArgs.HasFlag(args, "--anonymize"),
                PreferLargestSeries = CommandLineArgs.HasFlag(args, "--prefer-largest-series"),
                RequireStructures = CommandLineArgs.HasFlag(args, "--require-structures"),
                RequireDose = CommandLineArgs.HasFlag(args, "--require-dose"),
                SeriesDescriptionFilter = CommandLineArgs.OptionalArg(args, "--series-description"),
                StructDescriptionFilter = CommandLineArgs.OptionalArg(args, "--struct-description"),
                FailFast = CommandLineArgs.HasFlag(args, "--fail-fast"),
            };
            command.Options = options;

            ParseAnonymization(args, options);

            string manifestName = CommandLineArgs.OptionalArg(args, "--manifest-name");
            if (!string.IsNullOrEmpty(manifestName))
                options.ManifestFileName = manifestName;

            string patients = CommandLineArgs.OptionalArg(args, "--patients");
            if (!string.IsNullOrEmpty(patients))
                options.PatientIds = CohortExportOptions.ParseKeywordList(patients);

            options.SeriesInstanceUids = ParseList(args, SeriesFlag);
            options.StructureSetUids = ParseList(args, StructSeriesFlag);
            options.RoiSelection = ParseList(args, RoisFlag);

            // --target-spacing is accepted as an alias here for symmetry with --image-forward.
            string spacingArg = CommandLineArgs.OptionalArgAny(args, "--output-spacing", "--target-spacing");
            if (!string.IsNullOrEmpty(spacingArg))
            {
                if (!CohortExportOptions.TryParseSpacing(spacingArg, out var spacing, out string error))
                    throw new CommandLineUsageException($"--output-spacing: {error}");
                options.OutputSpacing = spacing;
            }

            ParseAssociations(args, command, log);

            if (mode == CohortMode.Convert)
            {
                options.ExportImages = !CommandLineArgs.HasFlag(args, "--no-images");
                options.ExportStructures = !CommandLineArgs.HasFlag(args, "--no-structures");
                options.ExportDoses = !CommandLineArgs.HasFlag(args, "--no-doses");

                options.ImageKeywords = ParseKeywords(args, "--metadata-tags",
                    MetadataTagCatalog.ImageComputedOptions);
                options.StructureKeywords = ParseKeywords(args, "--metadata-structure-tags",
                    MetadataTagCatalog.StructureComputedOptions);
                options.DoseKeywords = ParseKeywords(args, "--metadata-dose-tags",
                    MetadataTagCatalog.DoseComputedOptions);
            }
            else
            {
                options.ComputeVolumes = !CommandLineArgs.HasFlag(args, "--no-volumes");
            }

            command.JsonOutPath = CommandLineArgs.OptionalArg(args, "--json-out");
            command.DryRun = CommandLineArgs.HasFlag(args, DryRunFlag);
            return command;
        }

        /// <summary>--associations: reads the file into the command's options, and says so on <paramref name="log"/>.</summary>
        private static void ParseAssociations(string[] args, CohortCommand command, IProgress<string> log)
        {
            string associationsPath = CommandLineArgs.OptionalArg(args, "--associations");
            if (!string.IsNullOrEmpty(associationsPath))
            {
                if (!File.Exists(associationsPath))
                    throw new CommandLineUsageException($"Associations file not found: {associationsPath}");
                command.Options.Associations = new SettingsService().ImportAssociations(associationsPath);
                command.AssociationsPath = associationsPath;
                log?.Report($"Loaded {command.Options.Associations.Count} ROI association(s)");
            }
        }

        /// <summary>
        /// --tier: a tier name, ignoring case; header when absent. A tier this build does not
        /// compute yet is refused here, before any scan, like any other argument it cannot act on.
        /// </summary>
        private static AnalysisTier ParseTier(string[] args)
        {
            string text = CommandLineArgs.OptionalArg(args, TierFlag);
            if (text == null)
                return AnalysisTier.Header;
            if (!AnalysisTierNames.TryParse(text, out var tier))
            {
                throw new CommandLineUsageException(
                    $"{TierFlag}: expected {AnalysisTierNames.Header}, {AnalysisTierNames.Contours} or {AnalysisTierNames.Raster}; got '{text}'.");
            }
            if (!AnalysisTierNames.IsAvailable(tier))
            {
                throw new CommandLineUsageException(
                    $"{TierFlag} {AnalysisTierNames.Of(tier)} is not yet available; only {TierFlag} {AnalysisTierNames.Header} is.");
            }
            return tier;
        }

        /// <summary>--salt and --key-path; the latter only with --anonymize.</summary>
        private static void ParseAnonymization(string[] args, CohortExportOptions options)
        {
            string salt = CommandLineArgs.OptionalArg(args, "--salt");
            if (!string.IsNullOrEmpty(salt))
                options.Salt = salt;

            string keyPath = CommandLineArgs.OptionalArg(args, "--key-path");
            if (!string.IsNullOrEmpty(keyPath))
            {
                if (!options.Anonymize)
                    throw new CommandLineUsageException("--key-path places the anonymization key, so it needs --anonymize.");
                options.KeyPath = keyPath;
            }
        }

        /// <summary>
        /// A comma-separated list flag, trimmed, empties dropped. Given with nothing in it
        /// (<c>--rois ","</c>) it is refused: an empty selection means "no selection", which would
        /// silently widen the run to everything the flag was meant to narrow.
        /// </summary>
        private static List<string> ParseList(string[] args, string flag)
        {
            string text = CommandLineArgs.OptionalArg(args, flag);
            if (text == null)
                return new List<string>();
            var values = CohortExportOptions.ParseKeywordList(text);
            if (values.Count == 0)
                throw new CommandLineUsageException($"{flag}: expected a comma-separated list; got '{text}'.");
            return values;
        }

        private static List<string> ParseKeywords(
            string[] args, string flag, IReadOnlyList<ComputedMetadataOption> computedOptions)
        {
            string text = CommandLineArgs.OptionalArg(args, flag);
            if (!MetadataKeywordValidator.TryValidate(text, computedOptions, out var keywords, out string error))
                throw new CommandLineUsageException($"{flag}: {error}");
            return keywords;
        }

        // -----------------------------------------------------------------------------------
        //  Render
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The command line that reproduces <paramref name="options"/> under
        /// <paramref name="mode"/>. Options that carry associations need the file they live in;
        /// use the <see cref="CohortCommand"/> overload for those.
        /// </summary>
        public static string[] Render(CohortMode mode, CohortExportOptions options, PathPresenter presenter)
        {
            return Render(new CohortCommand { Mode = mode, Options = options }, presenter);
        }

        /// <summary>
        /// The command line that parses back to <paramref name="command"/>, mode flag first, every
        /// path through <paramref name="presenter"/>, and only the flags whose values differ from
        /// the defaults.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The command holds something its mode has no flag for (a metadata keyword on a survey,
        /// say), associations without the file they came from, or a value no command line can
        /// carry intact (a patient ID with a comma in it).
        /// </exception>
        public static string[] Render(CohortCommand command, PathPresenter presenter)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var o = command.Options ?? throw new ArgumentException("The command has no options.", nameof(command));
            var mode = command.Mode;
            presenter = presenter ?? CohortDocuments.Identity;

            if (string.IsNullOrEmpty(o.InputRoot))
                throw new ArgumentException("The options have no input folder.", nameof(command));
            var args = new List<string> { FlagFor(mode), "--input", presenter(o.InputRoot) };

            if (mode == CohortMode.Scan)
            {
                RequireDefault(mode, o.OutputRoot == null, "an output folder");
                RequireSurveyAndExportDefaults(mode, o);
                RequireDefault(mode, command.AssociationsPath == null && IsEmpty(o.Associations), "ROI associations");
                RequireDefault(mode, !o.OnlyAssociatedRois, "--only-associated-rois");
                RequireDefault(mode, o.OutputSpacing == null, "an output spacing");
                AddAnonymization(args, o, presenter);
                RequireDefault(mode, IsEmpty(o.PatientIds), "a patient filter");
                RequireDefault(mode, IsEmpty(o.SeriesInstanceUids), "a series selection");
                RequireDefault(mode, IsEmpty(o.StructureSetUids), "a structure-set choice");
                RequireDefault(mode, IsEmpty(o.RoiSelection), "an ROI selection");
                RequireDefault(mode, o.SeriesDescriptionFilter == null, "a series-description filter");
                RequireDefault(mode, o.StructDescriptionFilter == null, "a structure-set-description filter");
                RequireDefault(mode, !o.PreferLargestSeries, "--prefer-largest-series");
                RequireDefault(mode, !o.RequireStructures, "--require-structures");
                RequireDefault(mode, !o.RequireDose, "--require-dose");
                RequireDefault(mode, !o.FailFast, "--fail-fast");
                RequireDefault(mode, o.ManifestFileName == Defaults.ManifestFileName, "a manifest name");
                RequireDefault(mode, !command.DryRun, DryRunFlag);
                RequireAnalysisDefaults(mode, command);
                AddPath(args, "--json-out", command.JsonOutPath, presenter);
                return args.ToArray();
            }

            if (mode == CohortMode.Analyze)
            {
                RequireDefault(mode, o.OutputRoot == null, "an output folder");
                RequireSurveyAndExportDefaults(mode, o);
                RequireDefault(mode, !o.OnlyAssociatedRois, "--only-associated-rois");
                RequireDefault(mode, o.OutputSpacing == null, "an output spacing");
                RequireDefault(mode, IsEmpty(o.PatientIds), "a patient filter");
                RequireDefault(mode, IsEmpty(o.SeriesInstanceUids), "a series selection");
                RequireDefault(mode, IsEmpty(o.StructureSetUids), "a structure-set choice");
                RequireDefault(mode, IsEmpty(o.RoiSelection), "an ROI selection");
                RequireDefault(mode, o.SeriesDescriptionFilter == null, "a series-description filter");
                RequireDefault(mode, o.StructDescriptionFilter == null, "a structure-set-description filter");
                RequireDefault(mode, !o.PreferLargestSeries, "--prefer-largest-series");
                RequireDefault(mode, !o.RequireStructures, "--require-structures");
                RequireDefault(mode, !o.RequireDose, "--require-dose");
                RequireDefault(mode, !o.FailFast, "--fail-fast");
                RequireDefault(mode, o.ManifestFileName == Defaults.ManifestFileName, "a manifest name");
                RequireDefault(mode, !command.DryRun, DryRunFlag);
                if (!AnalysisTierNames.IsAvailable(command.Tier))
                {
                    throw new ArgumentException(
                        $"{TierFlag} {AnalysisTierNames.Of(command.Tier)} is not yet available, so no command line can carry it.", nameof(command));
                }

                if (command.Tier != AnalysisTier.Header)
                {
                    args.Add(TierFlag);
                    args.Add(AnalysisTierNames.Of(command.Tier));
                }
                AddAssociations(args, command, presenter);
                AddAnonymization(args, o, presenter);
                AddPath(args, "--json-out", command.JsonOutPath, presenter);
                AddPath(args, CsvOutFlag, command.CsvOutPath, presenter);
                return args.ToArray();
            }

            if (string.IsNullOrEmpty(o.OutputRoot))
                throw new ArgumentException("The options have no output folder.", nameof(command));
            args.Add("--output");
            args.Add(presenter(o.OutputRoot));

            AddAssociations(args, command, presenter);
            AddFlag(args, "--only-associated-rois", o.OnlyAssociatedRois);
            AddList(args, RoisFlag, o.RoiSelection, "ROI names");
            if (o.OutputSpacing != null)
            {
                args.Add("--output-spacing");
                args.Add(FormatSpacing(o.OutputSpacing));
            }
            AddAnonymization(args, o, presenter);
            if (!IsEmpty(o.PatientIds))
            {
                args.Add("--patients");
                args.Add(JoinList(o.PatientIds, "patient IDs"));
            }
            AddList(args, SeriesFlag, o.SeriesInstanceUids, "SeriesInstanceUIDs");
            AddValue(args, "--series-description", o.SeriesDescriptionFilter);
            AddValue(args, "--struct-description", o.StructDescriptionFilter);
            AddList(args, StructSeriesFlag, o.StructureSetUids, "structure-set SeriesInstanceUIDs");
            AddFlag(args, "--prefer-largest-series", o.PreferLargestSeries);
            AddFlag(args, "--require-structures", o.RequireStructures);
            AddFlag(args, "--require-dose", o.RequireDose);
            AddFlag(args, "--fail-fast", o.FailFast);
            if (o.ManifestFileName != Defaults.ManifestFileName)
            {
                if (string.IsNullOrEmpty(o.ManifestFileName))
                    throw new ArgumentException("An empty manifest name cannot be given on the command line.", nameof(command));
                args.Add("--manifest-name");
                args.Add(o.ManifestFileName);
            }

            if (mode == CohortMode.Convert)
            {
                RequireDefault(mode, o.ComputeVolumes, "--no-volumes");
                AddFlag(args, "--no-images", !o.ExportImages);
                AddFlag(args, "--no-structures", !o.ExportStructures);
                AddFlag(args, "--no-doses", !o.ExportDoses);
                AddKeywords(args, "--metadata-tags", o.ImageKeywords);
                AddKeywords(args, "--metadata-structure-tags", o.StructureKeywords);
                AddKeywords(args, "--metadata-dose-tags", o.DoseKeywords);
            }
            else
            {
                RequireExportDefaults(mode, o);
                AddFlag(args, "--no-volumes", !o.ComputeVolumes);
            }

            RequireAnalysisDefaults(mode, command);
            AddFlag(args, DryRunFlag, command.DryRun);
            AddPath(args, "--json-out", command.JsonOutPath, presenter);
            return args.ToArray();
        }

        /// <summary>The associations file, or a refusal when the options carry associations without one.</summary>
        private static void AddAssociations(List<string> args, CohortCommand command, PathPresenter presenter)
        {
            if (command.AssociationsPath != null)
                AddPath(args, "--associations", command.AssociationsPath, presenter);
            else if (!IsEmpty(command.Options.Associations))
                throw new ArgumentException(
                    "The options carry ROI associations but not the file they came from; render a " +
                    nameof(CohortCommand) + " with " + nameof(CohortCommand.AssociationsPath) + ".", nameof(command));
        }

        /// <summary>What only --cohort-analyze can say.</summary>
        private static void RequireAnalysisDefaults(CohortMode mode, CohortCommand command)
        {
            RequireDefault(mode, command.Tier == AnalysisTier.Header, TierFlag);
            RequireDefault(mode, command.CsvOutPath == null, CsvOutFlag);
        }

        private static void AddAnonymization(List<string> args, CohortExportOptions o, PathPresenter presenter)
        {
            AddFlag(args, "--anonymize", o.Anonymize);
            if (o.Salt != Defaults.Salt)
            {
                if (string.IsNullOrEmpty(o.Salt))
                    throw new ArgumentException("An empty salt cannot be given on the command line.", "command");
                args.Add("--salt");
                args.Add(o.Salt);
            }
            if (!string.IsNullOrEmpty(o.KeyPath))
                AddPath(args, "--key-path", o.KeyPath, presenter);
        }

        private static void RequireSurveyAndExportDefaults(CohortMode mode, CohortExportOptions o)
        {
            RequireDefault(mode, o.ComputeVolumes, "--no-volumes");
            RequireExportDefaults(mode, o);
        }

        /// <summary>What only --cohort-convert can say: what to leave out, and the metadata sidecar.</summary>
        private static void RequireExportDefaults(CohortMode mode, CohortExportOptions o)
        {
            RequireDefault(mode, o.ExportImages, "--no-images");
            RequireDefault(mode, o.ExportStructures, "--no-structures");
            RequireDefault(mode, o.ExportDoses, "--no-doses");
            RequireDefault(mode, IsEmpty(o.ImageKeywords), "--metadata-tags");
            RequireDefault(mode, IsEmpty(o.StructureKeywords), "--metadata-structure-tags");
            RequireDefault(mode, IsEmpty(o.DoseKeywords), "--metadata-dose-tags");
        }

        private static void RequireDefault(CohortMode mode, bool isDefault, string what)
        {
            if (!isDefault)
                throw new ArgumentException($"{FlagFor(mode)} has no flag for {what}.", "command");
        }

        private static bool IsEmpty<T>(List<T> list) => list == null || list.Count == 0;

        private static void AddFlag(List<string> args, string flag, bool present)
        {
            if (present)
                args.Add(flag);
        }

        private static void AddValue(List<string> args, string flag, string value)
        {
            if (value == null)
                return;
            args.Add(flag);
            args.Add(value);
        }

        private static void AddPath(List<string> args, string flag, string path, PathPresenter presenter)
        {
            if (string.IsNullOrEmpty(path))
                return;
            args.Add(flag);
            args.Add(presenter(path));
        }

        private static void AddKeywords(List<string> args, string flag, List<string> keywords) =>
            AddList(args, flag, keywords, "keywords");

        private static void AddList(List<string> args, string flag, List<string> values, string what)
        {
            if (IsEmpty(values))
                return;
            args.Add(flag);
            args.Add(JoinList(values, what));
        }

        /// <summary>
        /// A list as the comma-separated value <see cref="CohortExportOptions.ParseKeywordList"/>
        /// reads back: no entry may be empty, carry a comma, or begin or end with whitespace.
        /// </summary>
        private static string JoinList(List<string> values, string what)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value) || value.Contains(',') || value.Trim() != value)
                {
                    throw new ArgumentException(
                        $"'{value}' cannot be given in a comma-separated list of {what}.", "command");
                }
            }
            return string.Join(",", values);
        }

        /// <summary>"X,Y,Z" in round-trip form, the inverse of <see cref="CohortExportOptions.TryParseSpacing"/>.</summary>
        private static string FormatSpacing(double[] spacing)
        {
            if (spacing.Length != 3 || spacing.Any(v => !(v > 0) || double.IsInfinity(v)))
                throw new ArgumentException("An output spacing is three positive numbers in mm.", "command");
            return string.Join(",", spacing.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        }
    }
}
