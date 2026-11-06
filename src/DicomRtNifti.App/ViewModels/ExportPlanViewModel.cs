using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json.Linq;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// The dry run Convert Selected and Export Manifest Only show before they start: what the
    /// run will write and what it leaves out, and why. It is the plan document the CLI prints for
    /// <c>--dry-run</c> (<see cref="CohortPlanDocument"/>, built from
    /// <see cref="CohortExportService.BuildPlan"/>), read back for display, so the window shows
    /// exactly what Core decided: every folder after collision suffixing, the structure set chosen
    /// for each series and the mask names it gives, every dose, the warnings the run will log, and
    /// each skipped series with its reason. Nothing here decides anything.
    ///
    /// Under Anonymize export the document carries hashes only, as the export does; the key it was
    /// hashed with was opened read-only, so reviewing a plan writes nothing.
    /// </summary>
    public sealed class ExportPlanViewModel
    {
        public ExportPlanViewModel(
            CohortMode mode,
            CohortExportPlan plan,
            CohortExportOptions options,
            AnonymizationService readOnlyKey,
            string keyFile,
            string legacyKeyWarning)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));

            Mode = mode;
            IsConversion = mode == CohortMode.Convert;
            Json = CohortDocuments.Serialize(
                CohortPlanDocument.Build(mode, plan, options, readOnlyKey, keyFile, CohortDocuments.Identity));
            var document = JObject.Parse(Json);

            SeriesCount = (int?)document["series_count"] ?? 0;
            OutputRoot = (string)document["output_root"] ?? "";
            ManifestPath = Path.Combine(OutputRoot, (string)document["manifest_csv"] ?? "");
            KeyFile = (bool?)document["anonymized"] == true ? (string)document["anonymization_key"] : null;
            LegacyKeyWarning = legacyKeyWarning;

            bool writesDoses = IsConversion && options.ExportDoses;
            Series = Items(document["series"])
                .Select(s => new PlannedSeriesRow(s, IsConversion, options.ExportStructures, writesDoses)).ToList();
            Skipped = Items(document["skipped"]).Select(s => new LeftOutRow(s)).ToList();
            Unlinked = Items(document["unlinked"]).Select(s => new LeftOutRow(s)).ToList();
            RunWarnings = Strings(document["warnings"]);
        }

        public CohortMode Mode { get; }

        /// <summary>True for Convert Selected, false for Export Manifest Only.</summary>
        public bool IsConversion { get; }

        /// <summary>The plan document, as <c>--dry-run</c> prints it; what Copy as JSON copies.</summary>
        public string Json { get; }

        public int SeriesCount { get; }
        public string OutputRoot { get; }
        public string ManifestPath { get; }

        /// <summary>Where the run will write the anonymization key; null when not anonymizing.</summary>
        public string KeyFile { get; }

        /// <summary>A key an older export left inside the output folder, or null.</summary>
        public string LegacyKeyWarning { get; }

        public IReadOnlyList<PlannedSeriesRow> Series { get; }
        public IReadOnlyList<LeftOutRow> Skipped { get; }
        public IReadOnlyList<LeftOutRow> Unlinked { get; }

        /// <summary>What the run will warn about for the whole export (an ROI selection that matches nothing, say).</summary>
        public IReadOnlyList<string> RunWarnings { get; }

        public bool CanRun => SeriesCount > 0;
        public bool HasSkipped => Skipped.Count > 0;
        public bool HasUnlinked => Unlinked.Count > 0;
        public bool HasRunWarnings => RunWarnings.Count > 0;
        public bool HasKeyFile => KeyFile != null;
        public bool HasLegacyKeyWarning => !string.IsNullOrEmpty(LegacyKeyWarning);

        public string Title => IsConversion ? "Export plan: Convert Selected" : "Export plan: Export Manifest Only";

        public string Subtitle => IsConversion
            ? "What the conversion will write. Nothing is written until you click Convert."
            : "What the survey will measure. Only the manifest is written, once you click Export manifest.";

        /// <summary>"3 series to convert into C:\out", or why there is nothing to run.</summary>
        public string Headline
        {
            get
            {
                if (SeriesCount == 0)
                    return IsConversion
                        ? "Nothing to convert: every selected series is skipped (reasons below)."
                        : "Nothing to survey: every selected series is skipped (reasons below).";
                return string.Format(CultureInfo.InvariantCulture, "{0} series to {1} into {2}",
                    SeriesCount, IsConversion ? "convert" : "survey", OutputRoot);
            }
        }

        /// <summary>"1 skipped · 0 unlinked RT objects · 2 warnings".</summary>
        public string Counts
        {
            get
            {
                int warnings = RunWarnings.Count + Series.Sum(s => s.Warnings.Count);
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} skipped  ·  {1} unlinked RT object{2}  ·  {3} warning{4}",
                    Skipped.Count, Unlinked.Count, Unlinked.Count == 1 ? "" : "s",
                    warnings, warnings == 1 ? "" : "s");
            }
        }

        public string ManifestLine => "Manifest: " + ManifestPath + " (rows are merged into an existing file)";

        public string KeyLine => KeyFile == null ? null : "Anonymization key: " + KeyFile + " (outside the export)";

        /// <summary>The run button's caption: "Convert 3 series" or "Export manifest for 3 series".</summary>
        public string RunCaption => string.Format(CultureInfo.InvariantCulture,
            IsConversion ? "Convert {0} series" : "Export manifest for {0} series", SeriesCount);

        private static IEnumerable<JObject> Items(JToken token) =>
            token is JArray array ? array.OfType<JObject>() : Enumerable.Empty<JObject>();

        internal static List<string> Strings(JToken token) =>
            token is JArray array
                ? array.Select(t => (string)t).Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

        internal static string Short(string uid) =>
            string.IsNullOrEmpty(uid) ? "" : uid.Substring(0, Math.Min(8, uid.Length));
    }

    /// <summary>One series of the plan: its folder, what it is, and what will be written beside it.</summary>
    public sealed class PlannedSeriesRow
    {
        internal PlannedSeriesRow(JObject series, bool isConversion, bool exportStructures, bool writesDoses)
        {
            OutputDir = (string)series["output_dir"] ?? "";

            string description = (string)series["series_description"];
            Detail = string.Format(CultureInfo.InvariantCulture, "{0}  ·  {1}  ·  {2} slice{3}  ·  {4}",
                (string)series["modality"],
                string.IsNullOrWhiteSpace(description) ? "(no description)" : description.Trim(),
                (int?)series["instance_count"] ?? 0, ((int?)series["instance_count"] ?? 0) == 1 ? "" : "s",
                (string)series["patient_id"] + "/" + ExportPlanViewModel.Short((string)series["series_uid"]));

            var structureSet = series["structure_set"] as JObject;
            var rois = ExportPlanViewModel.Strings(series["rois"]);
            if (structureSet == null)
                Structures = "Structures: no structure set linked";
            else
            {
                string setName = (string)structureSet["series_description"];
                setName = string.IsNullOrWhiteSpace(setName) ? "(no description)" : setName.Trim();
                int roiCount = ExportPlanViewModel.Strings(structureSet["roi_names"]).Count;
                string what = isConversion ? "mask" : "volume";
                Structures = !exportStructures
                    ? $"Structures: {setName}, not used (Export Structures is off)"
                    : rois.Count == 0
                        ? $"Structures: {setName} ({roiCount} ROIs), none kept by the ROI selection, so no {what}"
                        : $"Structures: {setName} ({roiCount} ROIs) → {rois.Count} {what}{(rois.Count == 1 ? "" : "s")}: {string.Join(", ", rois)}";
            }

            var doses = (series["doses"] as JArray)?.OfType<JObject>()
                .Select(d => (string)d["series_description"])
                .Select(d => string.IsNullOrWhiteSpace(d) ? "(no description)" : d.Trim())
                .ToList() ?? new List<string>();
            Doses = doses.Count == 0
                ? "Doses: none linked"
                : !writesDoses
                    ? $"Doses: {doses.Count} linked, not written"
                    : $"Doses: {doses.Count} → {string.Join("; ", doses)}";

            Warnings = ExportPlanViewModel.Strings(series["warnings"]);
        }

        /// <summary>The series' folder relative to the output root, after collision suffixing.</summary>
        public string OutputDir { get; }
        public string Detail { get; }
        public string Structures { get; }
        public string Doses { get; }
        public IReadOnlyList<string> Warnings { get; }
        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>A skipped image series, or an RT object nothing will consume, with the reason.</summary>
    public sealed class LeftOutRow
    {
        internal LeftOutRow(JObject entry)
        {
            string description = (string)entry["series_description"];
            Series = string.Format(CultureInfo.InvariantCulture, "{0}/{1}  ·  {2}  ·  {3}",
                (string)entry["patient_id"], ExportPlanViewModel.Short((string)entry["series_instance_uid"]),
                (string)entry["modality"],
                string.IsNullOrWhiteSpace(description) ? "(no description)" : description.Trim());
            Reason = (string)entry["reason"] ?? "";
        }

        public string Series { get; }
        public string Reason { get; }
    }
}
