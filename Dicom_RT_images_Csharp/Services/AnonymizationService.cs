using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Dicom_RT_images_Csharp.Services
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
        public AnonymizationService(string keyFilePath, string salt)
        {
            _keyFilePath = keyFilePath;
            _salt = salt ?? "DicomToNifti";

            var keyFile = LoadKeyFile(_keyFilePath);
            if (keyFile != null)
            {
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
            string hash = DeterministicHashString("PATIENT:" + key, _salt, "P");
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
        /// Loads an AnonymizationKeyFile from the given path. Returns null if file does not exist or is invalid.
        /// </summary>
        public static AnonymizationKeyFile LoadKeyFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                var keyFile = JsonConvert.DeserializeObject<AnonymizationKeyFile>(json);
                return keyFile;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Saves an AnonymizationKeyFile to the given path as formatted JSON.
        /// </summary>
        public static void SaveKeyFile(string path, AnonymizationKeyFile keyFile)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };
            string json = JsonConvert.SerializeObject(keyFile, settings);
            File.WriteAllText(path, json);
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
