using System;
using DicomRtNifti.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// How a front-end shows an absolute path that a document carries: the input root, the output
    /// root, the manifest, the anonymization key. The CLI prints them as they are
    /// (<see cref="CohortDocuments.Identity"/>); a front-end that must not reveal where its data
    /// lives on disk can present them relative to a root of its own.
    /// </summary>
    public delegate string PathPresenter(string fullPath);

    /// <summary>
    /// What the cohort JSON documents share, whichever front-end prints them.
    ///
    /// The documents are a published contract: scripts and notebooks parse them, and every
    /// front-end must emit the same bytes for the same input. So they are built here, once, and
    /// a front-end only chooses how absolute paths are shown (<see cref="PathPresenter"/>).
    ///   * Every document has a "schema" field so consumers can version-check.
    ///   * Per-series paths are relative to the output root and use forward slashes; only the
    ///     roots, the manifest and the key are absolute.
    ///   * Under anonymization a document carries hashes only — never a source PatientID,
    ///     StudyInstanceUID, or file path. Re-identification lives solely in
    ///     AnonymizationKey.json, outside the output root.
    /// </summary>
    public static class CohortDocuments
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy(),
            },
            NullValueHandling = NullValueHandling.Include,
        };

        /// <summary>Shows every path as it is; the CLI's presenter.</summary>
        public static readonly PathPresenter Identity = path => path;

        /// <summary>
        /// Serializes a document. Anonymous types already use snake_case member names, so the
        /// naming strategy only normalizes the PascalCase members reached through Core model
        /// objects.
        /// </summary>
        public static string Serialize(object document) => JsonConvert.SerializeObject(document, JsonSettings);

        /// <summary><paramref name="path"/> through <paramref name="presenter"/>; null stays null.</summary>
        internal static string Present(PathPresenter presenter, string path) =>
            path == null ? null : (presenter ?? Identity)(path);

        internal static object DescribeSkipped(SkippedSeries s) => new
        {
            patient_id = s.PatientId,
            series_instance_uid = s.SeriesInstanceUid,
            modality = s.Modality,
            series_description = s.SeriesDescription,
            reason = s.Reason,
        };

        internal static object DescribeError(CohortError e) => new
        {
            patient_id = e.PatientId,
            series_uid = e.SeriesUid,
            message = e.Message,
        };

        internal static bool IsImageModality(string modality) =>
            string.Equals(modality, "CT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "MR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase);
    }
}
