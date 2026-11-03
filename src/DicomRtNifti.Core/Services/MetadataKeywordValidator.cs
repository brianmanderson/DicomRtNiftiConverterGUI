using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Checks the keywords asked of one metadata.json section before an export runs. Failing
    /// early beats writing a metadata.json full of nulls that reads as "the tag was absent from
    /// the DICOM" rather than "it was misspelled".
    ///
    /// Computed pseudo-keywords are validated against the section's own options, so asking for
    /// "@MaxDose" in the image section is reported rather than silently ignored: the image
    /// section has no dose to measure.
    /// </summary>
    public static class MetadataKeywordValidator
    {
        /// <summary>
        /// Splits a comma-separated keyword list (<see cref="CohortExportOptions.ParseKeywordList"/>)
        /// and checks every entry against the selectable DICOM tags and this section's computed
        /// values (<see cref="MetadataTagCatalog.ImageComputedOptions"/> and its siblings).
        /// </summary>
        /// <param name="keywords">The keywords in the order given; empty when the text is.</param>
        /// <param name="error">
        /// Why the list was refused, naming every unrecognised keyword and what the section
        /// accepts; null when it was accepted. A front-end prefixes its own name for the setting.
        /// </param>
        public static bool TryValidate(
            string text, IReadOnlyList<ComputedMetadataOption> computedOptions,
            out List<string> keywords, out string error)
        {
            keywords = CohortExportOptions.ParseKeywordList(text);
            error = null;
            if (keywords.Count == 0)
                return true;

            var knownTags = new HashSet<string>(
                DicomMetadataExtractor.GetSelectableTags().Select(t => t.Keyword),
                StringComparer.OrdinalIgnoreCase);
            var knownComputed = new HashSet<string>(
                computedOptions.Select(o => o.Key), StringComparer.OrdinalIgnoreCase);

            var unknown = keywords
                .Where(k => MetadataTagCatalog.IsComputedKey(k)
                    ? !knownComputed.Contains(k)
                    : !knownTags.Contains(k))
                .ToList();

            if (unknown.Count == 0)
                return true;

            error = $"unrecognised keyword(s) {string.Join(", ", unknown)}. "
                + "Expected fo-dicom tag keywords (PatientAge, KVP, DoseUnits) or one of this "
                + $"section's computed values ({string.Join(", ", knownComputed)}).";
            return false;
        }
    }
}
