using System.IO;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Documents
{
    /// <summary>
    /// The anonymization key, opened for a document that is printed and never acted on: a dry
    /// run's plan, or an anonymized scan. The hashes are deterministic and any mapping the key
    /// already holds (a manual override) is honoured, so the identifiers shown are the ones a real
    /// run would write; but nothing here ever calls <see cref="AnonymizationService.Save"/>, and
    /// unlike <see cref="AnonymizationKeyLocation.Open"/> a key an older export left inside the
    /// output root is read where it is rather than merged into a new file.
    /// </summary>
    public static class ReadOnlyAnonymizationKey
    {
        /// <summary>
        /// The key an export to <paramref name="outputRoot"/> would use, at the location it would
        /// resolve to (<see cref="AnonymizationKeyLocation.Resolve"/>). When that file does not
        /// exist yet but a key from an earlier export sits inside the output root, that one is
        /// read instead, as the real run would first copy its mappings across; when both exist the
        /// resolved key is read, and a mapping only the older key holds is not seen.
        /// </summary>
        /// <param name="keyFile">Full path where a real run would write the key.</param>
        /// <param name="legacyKeyWarning">
        /// Where the older key inside the export sits, when there is one; otherwise null.
        /// </param>
        /// <exception cref="System.ArgumentException">The key location is inside the export, or unusable.</exception>
        /// <exception cref="InvalidDataException">The key exists but does not parse.</exception>
        /// <exception cref="System.InvalidOperationException">The key records a different salt.</exception>
        public static AnonymizationService OpenForExport(
            string outputRoot, string keyPath, string salt, out string keyFile, out string legacyKeyWarning)
        {
            keyFile = AnonymizationKeyLocation.Resolve(outputRoot, keyPath);
            legacyKeyWarning = null;

            string source = keyFile;
            string legacy = AnonymizationKeyLocation.LegacyPathFor(outputRoot);
            if (File.Exists(legacy))
            {
                legacyKeyWarning =
                    $"An anonymization key from an earlier export is inside the export folder: '{legacy}'. " +
                    $"A real run carries its mappings into '{keyFile}'; this one only reads them. Move or " +
                    "delete the copy inside the export before sharing it: it re-identifies every patient.";
                if (!File.Exists(keyFile))
                    source = legacy;
            }

            return new AnonymizationService(source, salt);
        }

        /// <summary>
        /// A key read from <paramref name="keyFile"/> when one is given and exists there, or no key
        /// at all, in which case every hash is computed from <paramref name="salt"/> alone. For a
        /// command with no output folder to place a key beside, such as --cohort-scan.
        /// </summary>
        /// <exception cref="InvalidDataException">The key exists but does not parse.</exception>
        /// <exception cref="System.InvalidOperationException">The key records a different salt.</exception>
        public static AnonymizationService OpenFile(string keyFile, string salt)
        {
            string path = string.IsNullOrWhiteSpace(keyFile) ? null : AnonymizationKeyLocation.NormalizeKeyPath(keyFile);
            return new AnonymizationService(path, salt);
        }
    }
}
