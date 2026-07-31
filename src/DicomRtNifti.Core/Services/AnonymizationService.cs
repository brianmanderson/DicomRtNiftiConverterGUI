using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Provides deterministic per-identifier hashing for anonymized exports.
    /// Maintains three independent maps - MRN -> patient hash, StudyUID -> study hash,
    /// SeriesUID -> series hash - so a patient always resolves to the same patient hash and
    /// each new series gets its own series hash. The maps are persisted to AnonymizationKey.json
    /// for cross-session continuity and reverse lookup (de-anonymization).
    /// </summary>
    public class AnonymizationService
    {
        private readonly string _salt;
        private readonly string _keyFilePath;
        private readonly Dictionary<string, string> _patients; // mrn       -> patient hash
        private readonly Dictionary<string, string> _studies;  // studyUid  -> study hash
        private readonly Dictionary<string, string> _series;   // seriesUid -> series hash

        /// <summary>
        /// Creates a new AnonymizationService. If a key file in the current schema exists at the
        /// given path, loads it so previously assigned hashes are reused. Old-format key files
        /// (composite ExportID entries) deserialize to empty maps and are treated as a fresh start.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The key file exists but cannot be read or parsed. Constructing anyway would start from
        /// empty maps and the first <see cref="Save"/> would overwrite the damaged file, so every
        /// mapping it still held would be lost and already-exported patients would come back under
        /// a second pseudonym — the same patient in two identities is a train/validation leak.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The key file records a different salt than <paramref name="salt"/>; see
        /// <see cref="BuildSaltMismatchMessage"/>.
        /// </exception>
        public AnonymizationService(string keyFilePath, string salt)
        {
            _keyFilePath = keyFilePath;
            _salt = salt ?? "DicomToNifti";

            var keyFile = LoadKeyFile(_keyFilePath);
            if (keyFile != null)
            {
                EnsureSaltMatches(_keyFilePath, keyFile.Salt, _salt);
                _patients = keyFile.Patients ?? new Dictionary<string, string>();
                _studies = keyFile.Studies ?? new Dictionary<string, string>();
                _series = keyFile.Series ?? new Dictionary<string, string>();
            }
            else
            {
                _patients = new Dictionary<string, string>();
                _studies = new Dictionary<string, string>();
                _series = new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Produces a deterministic, filesystem-safe hash string from the input + salt.
        /// Same input and salt always produce the same output.
        /// Format: <paramref name="prefix"/> followed by the first <paramref name="bytes"/> bytes
        /// of SHA256 rendered as lowercase hex (so 6 bytes -> 12 hex characters).
        /// The prefix lets each identifier type (patient/study/series) be recognized at a glance and
        /// namespacing the input keeps the three types from ever colliding on the same value.
        /// </summary>
        public static string DeterministicHashString(string inputString, string salt, string prefix = "A", int bytes = 6)
        {
            string salted = string.Format("{0}:{1}", inputString, salt);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
                return prefix + BitConverter.ToString(hashBytes, 0, bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Returns the patient hash for an MRN. A mapping already present in the loaded key file
        /// (e.g. a manual override set in the editor) is honored; otherwise a deterministic hash is
        /// computed and recorded.
        /// </summary>
        public string GetPatientHash(string mrn)
        {
            string key = mrn ?? "";
            if (_patients.TryGetValue(key, out string existing))
                return existing;
            string hash = DeterministicHashString("PATIENT:" + key, _salt, "P", 5);
            _patients[key] = hash;
            return hash;
        }

        /// <summary>
        /// Returns the study hash for a StudyInstanceUID. Honors an existing/overridden mapping;
        /// otherwise computes and records a deterministic hash.
        /// </summary>
        public string GetStudyHash(string studyUid)
        {
            string key = studyUid ?? "";
            if (_studies.TryGetValue(key, out string existing))
                return existing;
            string hash = DeterministicHashString("STUDY:" + key, _salt, "ST");
            _studies[key] = hash;
            return hash;
        }

        /// <summary>
        /// Returns the series hash for a SeriesInstanceUID. Honors an existing/overridden mapping;
        /// otherwise computes and records a deterministic hash.
        /// </summary>
        public string GetSeriesHash(string seriesUid)
        {
            string key = seriesUid ?? "";
            if (_series.TryGetValue(key, out string existing))
                return existing;
            string hash = DeterministicHashString("SERIES:" + key, _salt, "SE");
            _series[key] = hash;
            return hash;
        }

        /// <summary>
        /// Saves the current key file to disk with all three maps (previous + new).
        /// </summary>
        public void Save()
        {
            var keyFile = new AnonymizationKeyFile
            {
                Salt = _salt,
                Patients = _patients,
                Studies = _studies,
                Series = _series
            };
            SaveKeyFile(_keyFilePath, keyFile);
        }

        /// <summary>
        /// Loads an AnonymizationKeyFile from the given path. Returns null when no file exists
        /// there — that is the legitimate "first run" case.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The file exists but cannot be read or does not parse as a key file. This used to be
        /// swallowed into a null return, which the caller could not tell apart from "no file yet":
        /// a truncated key file therefore silently became an empty one and was overwritten on the
        /// next save, losing every recorded mapping. Refusing loudly is the only safe answer,
        /// because the damage is irreversible and invisible in the export output.
        /// </exception>
        public static AnonymizationKeyFile LoadKeyFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(BuildUnusableKeyFileMessage(path, ex.Message), ex);
            }

            AnonymizationKeyFile keyFile;
            try
            {
                keyFile = JsonConvert.DeserializeObject<AnonymizationKeyFile>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(BuildUnusableKeyFileMessage(path, ex.Message), ex);
            }

            // An empty file (the classic truncation artefact) and a literal JSON null both
            // deserialize to null without throwing, so they need their own check.
            if (keyFile == null)
            {
                throw new InvalidDataException(BuildUnusableKeyFileMessage(
                    path, "the file is empty or contains a JSON null"));
            }

            return keyFile;
        }

        /// <summary>
        /// Rejects a caller salt that disagrees with the one the key file records.
        ///
        /// The salt is half of every hash the file contains. Continuing with a different one and
        /// then letting <see cref="Save"/> stamp the new salt into the file produces a key whose
        /// recorded salt no longer reproduces its own older entries, and an export in which some
        /// patients carry salt-A pseudonyms and the rest salt-B — the two sets are unlinkable, so
        /// the same patient can appear twice and no later run can tell which hash came from where.
        /// A changed salt is always a mistake or a deliberate restart; either way it needs a human.
        ///
        /// A key file that omits Salt deserializes to <see cref="AnonymizationKeyFile"/>'s default,
        /// which is the same "DicomToNifti" the callers default to, so files predating the field
        /// still load on an unconfigured install. An explicitly blank salt is not checkable and is
        /// accepted as-is.
        /// </summary>
        private static void EnsureSaltMatches(string path, string recordedSalt, string suppliedSalt)
        {
            if (string.IsNullOrEmpty(recordedSalt))
                return;
            if (string.Equals(recordedSalt, suppliedSalt, StringComparison.Ordinal))
                return;

            throw new InvalidOperationException(
                BuildSaltMismatchMessage(path, recordedSalt, suppliedSalt));
        }

        /// <summary>
        /// The message shown when the supplied salt disagrees with the key file's. Internal so the
        /// wording is covered by a test.
        /// </summary>
        internal static string BuildSaltMismatchMessage(string path, string recordedSalt, string suppliedSalt)
        {
            return
                $"Anonymization key file '{path}' was written with salt '{recordedSalt}', but this " +
                $"run supplies '{suppliedSalt}'. Refusing to continue: the salt is part of every " +
                "hash in that file, so re-using it under a different salt would give already-exported " +
                "patients a second, unlinkable pseudonym. Restore the original salt, or point at a " +
                "different key file to start a fresh anonymization.";
        }

        /// <summary>
        /// The message shown when an existing key file cannot be used. Names the path and the
        /// remedy, because the only safe recovery is a human decision: the mappings in a damaged
        /// key cannot be regenerated from the exported data.
        /// </summary>
        internal static string BuildUnusableKeyFileMessage(string path, string detail)
        {
            return
                $"Anonymization key file '{path}' exists but could not be read as a key file ({detail}). " +
                "Refusing to continue: starting from an empty key would assign a second pseudonym to " +
                "patients that were already exported, and the next save would overwrite whatever " +
                "mappings the file still holds. Move the file aside (or restore a backup) and re-run.";
        }

        /// <summary>
        /// Saves an AnonymizationKeyFile to the given path as formatted JSON.
        ///
        /// The write goes through <see cref="AtomicFileWriter"/>: a crash midway through a plain
        /// <c>File.WriteAllText</c> leaves the key truncated, which is exactly the state
        /// <see cref="LoadKeyFile"/> now has to refuse.
        /// </summary>
        public static void SaveKeyFile(string path, AnonymizationKeyFile keyFile)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };
            string json = JsonConvert.SerializeObject(keyFile, settings);
            AtomicFileWriter.WriteAllText(path, json);
        }
    }

    /// <summary>
    /// Represents the persisted anonymization key file: three reverse-lookup maps from original
    /// identifier to its deterministic hash.
    /// </summary>
    public class AnonymizationKeyFile
    {
        public string Salt { get; set; } = "DicomToNifti";

        /// <summary>MRN -> patient hash.</summary>
        public Dictionary<string, string> Patients { get; set; } = new Dictionary<string, string>();

        /// <summary>StudyInstanceUID -> study hash.</summary>
        public Dictionary<string, string> Studies { get; set; } = new Dictionary<string, string>();

        /// <summary>SeriesInstanceUID -> series hash.</summary>
        public Dictionary<string, string> Series { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Represents one row in the export manifest CSV. The PatientID/StudyUID/SeriesUID fields hold
    /// the anonymization hashes when anonymizing and the real identifiers otherwise.
    /// </summary>
    public class ManifestRow
    {
        public string PatientID { get; set; }
        public string StudyUID { get; set; }
        public string SeriesUID { get; set; }
        public double SpacingX { get; set; }
        public double SpacingY { get; set; }
        public double SpacingZ { get; set; }
        /// <summary>
        /// ROI canonical name -> mask volume (voxelCount * spacingX * spacingY * spacingZ).
        /// Missing ROIs default to -1 at CSV write time.
        /// </summary>
        public Dictionary<string, double> RoiVolumes { get; set; } = new Dictionary<string, double>();
    }
}
