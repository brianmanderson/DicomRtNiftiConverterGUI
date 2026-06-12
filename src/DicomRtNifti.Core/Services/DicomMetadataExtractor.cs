using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DicomRtNifti.Core.Models;
using FellowOakDicom;
using FellowOakDicom.IO;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Services
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
        /// Writes a sectioned <c>metadata.json</c> for one series. Each non-empty keyword list in
        /// <paramref name="request"/> becomes a top-level section — "ImageAttributes",
        /// "StructureAttributes", "DoseAttributes" — keyed by friendly names
        /// (<see cref="MetadataTagCatalog.FriendlyName"/>); a section whose source file is missing
        /// is written with all-null values. Computed "@..." keys are resolved per section. Never
        /// throws on a missing/unreadable source file (those values fall back to null); only a
        /// failure to write <paramref name="outputJsonPath"/> propagates.
        /// </summary>
        public static void WriteMetadataJson(MetadataExportRequest request, string outputJsonPath)
        {
            if (request == null)
                return;

            var root = new Dictionary<string, object>();

            if (request.ImageKeywords != null && request.ImageKeywords.Count > 0)
            {
                string imagePath = (request.ImageFilePaths != null && request.ImageFilePaths.Count > 0)
                    ? request.ImageFilePaths[0] : null;
                int sliceCount = request.ImageFilePaths != null ? request.ImageFilePaths.Count : 0;
                var ds = TryOpen(imagePath, FileReadOption.ReadLargeOnDemand);
                Func<string, object> resolver = key =>
                {
                    if (key == MetadataTagCatalog.VoxelSizeKey)
                        return MetadataComputedValues.ImageVoxelSize(ds, request.ImageVoxelSpacing);
                    if (key == MetadataTagCatalog.ImageDimensionsKey)
                        return MetadataComputedValues.ImageDimensions(ds, sliceCount);
                    return null;
                };
                root["ImageAttributes"] = BuildSection(ds, request.ImageKeywords, resolver);
            }

            if (request.StructureKeywords != null && request.StructureKeywords.Count > 0)
            {
                var ds = TryOpen(request.StructureFilePath, FileReadOption.ReadLargeOnDemand);
                Func<string, object> resolver = key =>
                {
                    if (key == MetadataTagCatalog.RoiNamesKey)
                        return MetadataComputedValues.RoiNames(ds);
                    if (key == MetadataTagCatalog.RoiCountKey)
                        return MetadataComputedValues.RoiCount(ds);
                    return null;
                };
                root["StructureAttributes"] = BuildSection(ds, request.StructureKeywords, resolver);
            }

            if (request.DoseKeywords != null && request.DoseKeywords.Count > 0)
            {
                // Max dose needs the pixel buffer materialized, so read the whole file for that case.
                bool needsPixels = ListContains(request.DoseKeywords, MetadataTagCatalog.MaxDoseKey);
                var ds = TryOpen(request.DoseFilePath,
                    needsPixels ? FileReadOption.ReadAll : FileReadOption.ReadLargeOnDemand);
                Func<string, object> resolver = key =>
                {
                    if (key == MetadataTagCatalog.MaxDoseKey)
                        return MetadataComputedValues.MaxDose(ds);
                    if (key == MetadataTagCatalog.DoseVoxelSizeKey)
                        return MetadataComputedValues.DoseVoxelSize(ds);
                    return null;
                };
                root["DoseAttributes"] = BuildSection(ds, request.DoseKeywords, resolver);
            }

            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            File.WriteAllText(outputJsonPath, json);
        }

        // Opens a DICOM file's dataset, or returns null if the path is missing/unreadable.
        private static DicomDataset TryOpen(string path, FileReadOption option)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            try { return DicomFile.Open(path, option).Dataset; }
            catch { return null; }
        }

        private static bool ListContains(IReadOnlyList<string> keywords, string key)
        {
            if (keywords == null)
                return false;
            for (int i = 0; i < keywords.Count; i++)
                if (string.Equals(keywords[i], key, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Builds one friendly-name-keyed section. Computed "@..." keys are resolved via
        /// <paramref name="computedResolver"/>; raw keywords are read from <paramref name="dataset"/>
        /// (null dataset → null values). Keys are mapped through
        /// <see cref="MetadataTagCatalog.FriendlyName"/>; a (theoretical) friendly-name collision
        /// falls back to the raw keyword as the key.
        /// </summary>
        private static Dictionary<string, object> BuildSection(
            DicomDataset dataset, IReadOnlyList<string> keywords, Func<string, object> computedResolver)
        {
            var section = new Dictionary<string, object>();
            if (keywords == null)
                return section;

            var map = KeywordToTag.Value;
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrEmpty(keyword))
                    continue;

                object value;
                if (MetadataTagCatalog.IsComputedKey(keyword))
                {
                    value = computedResolver != null ? computedResolver(keyword) : null;
                }
                else if (dataset != null && map.TryGetValue(keyword, out var tag))
                {
                    value = ExtractValue(dataset, tag);
                }
                else
                {
                    value = null; // missing dataset or unknown keyword: keep the key, null value
                }

                string friendly = MetadataTagCatalog.FriendlyName(keyword);
                if (string.IsNullOrEmpty(friendly))
                    friendly = keyword;

                if (!section.ContainsKey(friendly))
                    section[friendly] = value;
                else if (!section.ContainsKey(keyword))
                    section[keyword] = value; // friendly-name collision: fall back to raw keyword
            }
            return section;
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
