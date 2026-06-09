using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;
using FellowOakDicom.IO;
using Newtonsoft.Json;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Reads a chosen set of DICOM attributes from a file and serializes them to a
    /// <c>metadata.json</c> sidecar. All FellowOakDicom and JSON access lives here so the
    /// Avalonia view-models stay free of those dependencies.
    ///
    /// Selectable tags come from fo-dicom's standard data dictionary
    /// (<see cref="DicomDictionary.Default"/>); the JSON key is the tag's PascalCase keyword
    /// (e.g. "PatientName") and the value is typed by VR: integer VRs (US/SS/UL/SL/IS) become
    /// JSON integers, real VRs (FL/FD/DS) become JSON numbers, everything else stays a string.
    /// Multi-valued tags become JSON arrays; tags absent from the file are written as null so
    /// every export folder gets the same key set.
    /// </summary>
    public static class DicomMetadataExtractor
    {
        // VRs that carry no human-readable scalar value (sequences and bulk/binary data); hidden
        // from the picker so users can only choose tags that serialize meaningfully.
        private static readonly HashSet<string> ExcludedVrCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SQ", "OB", "OW", "OF", "OD", "OL", "OV", "UN", "NONE"
        };

        // Integer- and real-valued VRs. IS/DS are string-encoded numbers in DICOM but are emitted
        // as JSON numbers here so downstream consumers get 3 / 1.25 rather than "3" / "1.25".
        private static readonly HashSet<string> IntegerVrCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "US", "SS", "UL", "SL", "IS"
        };
        private static readonly HashSet<string> RealVrCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "FL", "FD", "DS"
        };

        // Built once from the (immutable) default dictionary; thread-safe via Lazy.
        private static readonly Lazy<List<MetadataTagOption>> SelectableTags =
            new Lazy<List<MetadataTagOption>>(BuildSelectableTags);
        private static readonly Lazy<Dictionary<string, DicomTag>> KeywordToTag =
            new Lazy<Dictionary<string, DicomTag>>(BuildKeywordToTag);

        /// <summary>
        /// All standard DICOM attributes a user can pick, sorted by keyword. Excludes private,
        /// repeating-group (masked), group-length, item-delimiter, and non-scalar (sequence/binary)
        /// tags. Cached after the first call.
        /// </summary>
        public static IReadOnlyList<MetadataTagOption> GetSelectableTags() => SelectableTags.Value;

        /// <summary>
        /// Reads <paramref name="keywords"/> from <paramref name="dicomFilePath"/> and writes them
        /// to <paramref name="outputJsonPath"/> as indented JSON. Only the header is loaded eagerly
        /// (pixel data is read on demand and never touched here). Throws if the file cannot be opened.
        /// </summary>
        public static void WriteMetadataJson(string dicomFilePath, IReadOnlyList<string> keywords, string outputJsonPath)
        {
            var dataset = DicomFile.Open(dicomFilePath, FileReadOption.ReadLargeOnDemand).Dataset;
            var metadata = Extract(dataset, keywords);
            string json = JsonConvert.SerializeObject(metadata, Formatting.Indented);
            File.WriteAllText(outputJsonPath, json);
        }

        /// <summary>
        /// Builds an ordered keyword → value map for the requested tags. Order follows
        /// <paramref name="keywords"/>; duplicates and unknown keywords are handled gracefully
        /// (unknown keywords map to null). Values are typed per the class summary.
        /// </summary>
        public static Dictionary<string, object> Extract(DicomDataset dataset, IReadOnlyList<string> keywords)
        {
            var result = new Dictionary<string, object>();
            if (dataset == null || keywords == null)
                return result;

            var map = KeywordToTag.Value;
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrEmpty(keyword) || result.ContainsKey(keyword))
                    continue;

                if (!map.TryGetValue(keyword, out var tag))
                {
                    result[keyword] = null; // unknown keyword: keep the key, no value
                    continue;
                }
                result[keyword] = ExtractValue(dataset, tag);
            }
            return result;
        }

        /// <summary>
        /// Extracts one tag's value as a JSON-friendly object: a boxed long/double/string for a
        /// single value, an array for a multi-valued tag, or null when the tag is absent/empty.
        /// Never throws — any read failure falls back to the raw string, then to null.
        /// </summary>
        private static object ExtractValue(DicomDataset dataset, DicomTag tag)
        {
            if (!dataset.Contains(tag))
                return null;

            try
            {
                string vrCode = dataset.GetDicomItem<DicomItem>(tag)?.ValueRepresentation?.Code ?? "";

                // Read the value(s) as strings — fo-dicom's most universal getter, working across
                // both string-backed (IS/DS/PN/…) and binary (US/FD/…) VRs — then type by VR. (A
                // typed getter like TryGetValues<long> does not convert binary US/SS to long.)
                if (!dataset.TryGetValues<string>(tag, out string[] raw) || raw.Length == 0)
                {
                    if (dataset.TryGetString(tag, out string combined) && !string.IsNullOrEmpty(combined))
                        return combined;
                    return null;
                }

                object RawResult() => raw.Length == 1 ? (object)raw[0] : raw;

                if (IntegerVrCodes.Contains(vrCode))
                {
                    var values = new long[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                        if (!long.TryParse(raw[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                            return RawResult(); // unexpected non-integer text: keep the raw string(s)
                    return values.Length == 1 ? (object)values[0] : values;
                }

                if (RealVrCodes.Contains(vrCode))
                {
                    var values = new double[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                        if (!double.TryParse(raw[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                            return RawResult();
                    return values.Length == 1 ? (object)values[0] : values;
                }

                return RawResult();
            }
            catch
            {
                try { return dataset.TryGetString(tag, out string s) && !string.IsNullOrEmpty(s) ? (object)s : null; }
                catch { return null; }
            }
        }

        private static List<MetadataTagOption> BuildSelectableTags()
        {
            var list = new List<MetadataTagOption>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in DicomDictionary.Default)
            {
                if (entry == null || entry.MaskTag != null)
                    continue;

                var tag = entry.Tag;
                if (tag == null || tag.IsPrivate || tag.Element == 0x0000 || tag.Group == 0xFFFE)
                    continue;

                string keyword = entry.Keyword;
                if (string.IsNullOrEmpty(keyword))
                    continue;

                string vrCode = (entry.ValueRepresentations != null && entry.ValueRepresentations.Length > 0)
                    ? entry.ValueRepresentations[0].Code
                    : "";
                if (ExcludedVrCodes.Contains(vrCode))
                    continue;

                if (!seen.Add(keyword))
                    continue;

                list.Add(new MetadataTagOption
                {
                    Keyword = keyword,
                    Name = entry.Name ?? "",
                    Group = tag.Group,
                    Element = tag.Element,
                    VrCode = vrCode
                });
            }

            list.Sort((a, b) => string.Compare(a.Keyword, b.Keyword, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static Dictionary<string, DicomTag> BuildKeywordToTag()
        {
            var map = new Dictionary<string, DicomTag>(StringComparer.Ordinal);
            foreach (var entry in DicomDictionary.Default)
            {
                if (entry == null || entry.MaskTag != null)
                    continue;
                string keyword = entry.Keyword;
                if (string.IsNullOrEmpty(keyword) || map.ContainsKey(keyword))
                    continue;
                map[keyword] = entry.Tag;
            }
            return map;
        }
    }
}
