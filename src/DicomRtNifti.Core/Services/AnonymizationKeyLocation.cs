using System;
using System.Collections.Generic;
using System.IO;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Where an anonymized export keeps its <c>AnonymizationKey.json</c>: never inside the export.
    ///
    /// The key maps every source MRN, StudyInstanceUID and SeriesInstanceUID to its hash, which is
    /// what lets an anonymized export be traced back to the patients it came from. It used to be
    /// written into the export root, and an export is usually handed on by zipping or uploading
    /// that folder as it stands, which shipped the re-identification key with the pseudonymized
    /// data (#54). The key is now always written, by default to a sibling folder beside the export,
    /// <c>&lt;output&gt;.key/AnonymizationKey.json</c>. An explicit file or folder may be given
    /// instead, and any location at or under the export root is refused. Every front-end goes
    /// through here: the CLI's cohort modes, <see cref="CohortExportService"/>, and the GUI's
    /// Convert Selected, Export Manifest Only and key editor.
    ///
    /// A key that an earlier export left inside the export root is not ignored: <see cref="Open"/>
    /// carries its mappings, manual overrides included, into the key at the new location, and
    /// warns that the old copy is still inside the export. It is never deleted automatically:
    /// it may be the only copy of those mappings.
    /// </summary>
    public static class AnonymizationKeyLocation
    {
        public const string FileName = "AnonymizationKey.json";

        /// <summary>Appended to the export folder's own name to form the default key folder.</summary>
        public const string SiblingFolderSuffix = ".key";

        /// <summary>
        /// The default key location for <paramref name="outputRoot"/>:
        /// <c>&lt;outputRoot&gt;.key/AnonymizationKey.json</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The output root is a volume root (<c>D:\</c>, <c>/</c>), which has no sibling folder.
        /// </exception>
        public static string DefaultFor(string outputRoot)
        {
            string root = NormalizeRoot(outputRoot);
            if (string.IsNullOrEmpty(Path.GetDirectoryName(root)))
            {
                throw new ArgumentException(
                    $"The export folder '{root}' is the root of a volume, so there is no folder beside it " +
                    "for the anonymization key. Choose a key location outside the export.",
                    nameof(outputRoot));
            }
            return Path.Combine(root + SiblingFolderSuffix, FileName);
        }

        /// <summary>
        /// The key file for an export to <paramref name="outputRoot"/>: <paramref name="keyPath"/>
        /// when given (see <see cref="NormalizeKeyPath"/>), otherwise <see cref="DefaultFor"/>.
        /// Returns a full path.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// No output root was given, or the key would land at or under it.
        /// </exception>
        public static string Resolve(string outputRoot, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
                throw new ArgumentException("An output folder is needed to place the anonymization key.", nameof(outputRoot));

            string resolved = string.IsNullOrWhiteSpace(keyPath)
                ? DefaultFor(outputRoot)
                : NormalizeKeyPath(keyPath);

            if (IsWithin(resolved, outputRoot))
                throw new ArgumentException(BuildInsideExportMessage(resolved, outputRoot), nameof(keyPath));
            return resolved;
        }

        /// <summary>
        /// A user-supplied key location as a full file path. A path that names an existing folder,
        /// or ends in a directory separator, gets <see cref="FileName"/> appended; anything else is
        /// taken as the key file itself.
        /// </summary>
        public static string NormalizeKeyPath(string keyPath)
        {
            if (string.IsNullOrWhiteSpace(keyPath))
                throw new ArgumentException("The anonymization key path is empty.", nameof(keyPath));

            string trimmed = keyPath.Trim();
            string full = Path.GetFullPath(trimmed);
            bool namesFolder = Directory.Exists(full)
                || trimmed.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || trimmed.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
            return namesFolder ? Path.Combine(Path.TrimEndingDirectorySeparator(full), FileName) : full;
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="root"/> or lies anywhere beneath it,
        /// after both are made full (so <c>..</c> segments and trailing separators cannot hide it).
        /// Case-insensitive except on Linux, matching the default file systems.
        /// </summary>
        public static bool IsWithin(string path, string root)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string rootFull = NormalizeRoot(root);
            if (string.Equals(full, rootFull, PathComparison))
                return true;

            string prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, PathComparison);
        }

        /// <summary>Where every export before #54 wrote the key: the export root itself.</summary>
        public static string LegacyPathFor(string outputRoot) => Path.Combine(NormalizeRoot(outputRoot), FileName);

        /// <summary>
        /// Opens the anonymization key for an export to <paramref name="outputRoot"/> at its resolved
        /// location (<see cref="Resolve"/>). When an earlier export left a key inside the export
        /// root, its mappings are first merged into the key at the new location, and
        /// <paramref name="legacyKeyWarning"/> says where the old copy still sits; otherwise it is null.
        /// The returned service's <see cref="AnonymizationService.KeyFilePath"/> is the key's full path.
        /// </summary>
        /// <exception cref="ArgumentException">The key location is inside the export, or unusable.</exception>
        /// <exception cref="InvalidDataException">The key, or the legacy key, exists but does not parse.</exception>
        /// <exception cref="InvalidOperationException">
        /// A salt disagrees with the one a key records, or the legacy key and the key map the same
        /// identifier to different hashes; either needs a human to decide.
        /// </exception>
        public static AnonymizationService Open(
            string outputRoot, string keyPath, string salt, out string legacyKeyWarning)
        {
            string resolved = Resolve(outputRoot, keyPath);
            legacyKeyWarning = null;

            string legacy = LegacyPathFor(outputRoot);
            if (File.Exists(legacy))
            {
                MergeLegacyKey(legacy, resolved, salt);
                legacyKeyWarning = BuildLegacyKeyWarning(legacy, resolved);
            }

            return new AnonymizationService(resolved, salt);
        }

        /// <summary>
        /// Carries every mapping of the key at <paramref name="legacyPath"/> into the key at
        /// <paramref name="keyPath"/>, creating it when absent. Idempotent: a second call adds
        /// nothing and writes nothing. The legacy file is left where it is.
        /// </summary>
        internal static void MergeLegacyKey(string legacyPath, string keyPath, string salt)
        {
            salt = salt ?? AnonymizationService.DefaultSalt;
            var legacy = AnonymizationService.LoadKeyFile(legacyPath);
            AnonymizationService.EnsureKeyFileSaltMatches(legacyPath, salt);

            var target = AnonymizationService.LoadKeyFile(keyPath);
            bool created = target == null;
            if (created)
                target = new AnonymizationKeyFile { Salt = salt };
            else
                AnonymizationService.EnsureKeyFileSaltMatches(keyPath, salt);

            if (target.Patients == null) target.Patients = new Dictionary<string, string>();
            if (target.Studies == null) target.Studies = new Dictionary<string, string>();
            if (target.Series == null) target.Series = new Dictionary<string, string>();

            bool changed = MergeMap(legacy.Patients, target.Patients, "patient", legacyPath, keyPath);
            changed |= MergeMap(legacy.Studies, target.Studies, "study", legacyPath, keyPath);
            changed |= MergeMap(legacy.Series, target.Series, "series", legacyPath, keyPath);

            if (created || changed)
                AnonymizationService.SaveKeyFile(keyPath, target);
        }

        private static bool MergeMap(
            Dictionary<string, string> from, Dictionary<string, string> into, string level,
            string legacyPath, string keyPath)
        {
            if (from == null)
                return false;

            bool changed = false;
            foreach (var entry in from)
            {
                if (into.TryGetValue(entry.Key, out string existing))
                {
                    // Two pseudonyms for one identifier is the train/validation leak the key exists
                    // to prevent; which one is right is a human's call. The identifier itself stays
                    // out of the message, which reaches logs and stderr.
                    if (!string.Equals(existing, entry.Value, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"The anonymization key inside the export ('{legacyPath}') maps a {level} identifier " +
                            $"to '{entry.Value}', but the key at '{keyPath}' maps it to '{existing}'. Refusing to " +
                            "continue: one identifier with two pseudonyms cannot be linked back. Reconcile the two " +
                            "files by hand (the Edit Anonymization Key editor opens either), then re-run.");
                    }
                    continue;
                }
                into[entry.Key] = entry.Value;
                changed = true;
            }
            return changed;
        }

        internal static string BuildInsideExportMessage(string keyPath, string outputRoot)
        {
            return
                $"The anonymization key would be written to '{keyPath}', inside the export folder " +
                $"'{NormalizeRoot(outputRoot)}'. The key re-identifies every patient in the export, so it " +
                "must not travel with it. Choose a location outside the export, or leave the key location " +
                $"empty to use the default '{SafeDefault(outputRoot)}'.";
        }

        internal static string BuildLegacyKeyWarning(string legacyPath, string keyPath)
        {
            return
                $"An anonymization key from an earlier export is inside the export folder: '{legacyPath}'. " +
                $"Its mappings were carried into '{keyPath}', where this export's key is written. Move or " +
                "delete the copy inside the export before sharing it: it re-identifies every patient.";
        }

        private static string SafeDefault(string outputRoot)
        {
            try { return DefaultFor(outputRoot); }
            catch (ArgumentException) { return "<output folder>" + SiblingFolderSuffix; }
        }

        private static string NormalizeRoot(string outputRoot) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));

        private static StringComparison PathComparison =>
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }
}
