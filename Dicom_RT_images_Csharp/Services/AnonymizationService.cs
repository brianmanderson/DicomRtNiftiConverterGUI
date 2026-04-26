using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Provides deterministic hashing for anonymized exports and
    /// assigns incrementing integer export IDs to unique hash keys.
    /// Persists mappings to AnonymizationKey.json for cross-session continuity.
    /// </summary>
    public class AnonymizationService
    {
        private readonly string _salt;
        private readonly string _keyFilePath;
        private readonly Dictionary<string, AnonymizationKeyEntry> _entries;
        private int _nextExportId;
        private readonly List<ManifestRow> _sessionManifestRows = new List<ManifestRow>();

        /// <summary>
        /// Creates a new AnonymizationService. If a key file exists at the given path,
        /// loads it to resume numbering from the previous session.
        /// </summary>
        public AnonymizationService(string keyFilePath, string salt)
        {
            _keyFilePath = keyFilePath;
            _salt = salt ?? "DicomToNifti";

            // Load existing key file if present
            var keyFile = LoadKeyFile(_keyFilePath);
            if (keyFile != null)
            {
                _entries = keyFile.Entries ?? new Dictionary<string, AnonymizationKeyEntry>();
                _nextExportId = keyFile.NextExportID;
            }
            else
            {
                _entries = new Dictionary<string, AnonymizationKeyEntry>();
                _nextExportId = 0;
            }
        }

        /// <summary>
        /// Produces a deterministic 9-character string from the input + salt.
        /// Same input and salt always produce the same output.
        /// Format: "A" followed by 8 hex characters (first 4 bytes of SHA256).
        /// </summary>
        public static string DeterministicHashString(string inputString, string salt)
        {
            string salted = string.Format("{0}:{1}", inputString, salt);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
                return "A" + BitConverter.ToString(hashBytes, 0, 4).Replace("-", "");
            }
        }

        /// <summary>
        /// Gets or assigns an integer export ID for the given MRN/StudyUID/SeriesUID combination.
        /// The hash key is "{mrn}_{studyUid}_{seriesUid}".
        /// If this combination was seen in a previous session (loaded from key file), returns the same ID.
        /// Otherwise assigns the next available integer ID.
        /// Also records a manifest row for CSV output.
        /// </summary>
        public int GetOrAssignExportId(string mrn, string studyUid, string seriesUid)
        {
            string hashKey = string.Format("{0}_{1}_{2}", mrn, studyUid, seriesUid);
            string hashString = DeterministicHashString(hashKey, _salt);

            int exportId;
            AnonymizationKeyEntry entry;
            if (_entries.TryGetValue(hashString, out entry))
            {
                exportId = entry.ExportID;
            }
            else
            {
                exportId = _nextExportId++;
                _entries[hashString] = new AnonymizationKeyEntry
                {
                    ExportID = exportId,
                    MRN = mrn,
                    StudyUID = studyUid,
                    SeriesUID = seriesUid
                };
            }

            _sessionManifestRows.Add(new ManifestRow
            {
                MRN = mrn,
                StudyUID = studyUid,
                SeriesUID = seriesUid,
                ExportID = exportId
            });

            return exportId;
        }

        /// <summary>
        /// Saves the current key file to disk with all entries (previous + new).
        /// </summary>
        public void Save()
        {
            var keyFile = new AnonymizationKeyFile
            {
                NextExportID = _nextExportId,
                Salt = _salt,
                Entries = _entries
            };
            SaveKeyFile(_keyFilePath, keyFile);
        }

        /// <summary>
        /// Returns all manifest rows accumulated during this session (for CSV output).
        /// </summary>
        public List<ManifestRow> GetSessionManifestRows()
        {
            return _sessionManifestRows;
        }

        /// <summary>
        /// Returns ALL entries (previous + current session) for a full manifest CSV.
        /// </summary>
        public List<ManifestRow> GetAllManifestRows()
        {
            return _entries.Values
                .OrderBy(e => e.ExportID)
                .Select(e => new ManifestRow
                {
                    MRN = e.MRN,
                    StudyUID = e.StudyUID,
                    SeriesUID = e.SeriesUID,
                    ExportID = e.ExportID
                })
                .ToList();
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
    /// Represents the persisted anonymization key file.
    /// </summary>
    public class AnonymizationKeyFile
    {
        public int NextExportID { get; set; } = 0;
        public string Salt { get; set; } = "DicomToNifti";
        public Dictionary<string, AnonymizationKeyEntry> Entries { get; set; }
            = new Dictionary<string, AnonymizationKeyEntry>();
    }

    /// <summary>
    /// A single entry in the anonymization key file.
    /// </summary>
    public class AnonymizationKeyEntry
    {
        public int ExportID { get; set; }
        public string MRN { get; set; }
        public string StudyUID { get; set; }
        public string SeriesUID { get; set; }
    }

    /// <summary>
    /// Represents one row in the export manifest CSV.
    /// </summary>
    public class ManifestRow
    {
        public string MRN { get; set; }
        public string StudyUID { get; set; }
        public string SeriesUID { get; set; }
        public int ExportID { get; set; }
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
