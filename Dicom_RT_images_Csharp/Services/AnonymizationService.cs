using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Provides deterministic hashing for anonymized exports and
    /// assigns incrementing integer export IDs to unique hash keys.
    /// </summary>
    public class AnonymizationService
    {
        private readonly string _salt;
        private readonly Dictionary<string, int> _hashToExportId = new Dictionary<string, int>();
        private int _nextExportId = 0;
        private readonly List<ManifestRow> _manifestRows = new List<ManifestRow>();

        public AnonymizationService(string salt)
        {
            _salt = salt ?? "DicomToNifti";
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
        /// Each unique hash string gets the next available integer ID (starting at 0).
        /// Also records a manifest row for CSV output.
        /// </summary>
        public int GetOrAssignExportId(string mrn, string studyUid, string seriesUid)
        {
            string hashKey = string.Format("{0}_{1}_{2}", mrn, studyUid, seriesUid);
            string hashString = DeterministicHashString(hashKey, _salt);

            int exportId;
            if (!_hashToExportId.TryGetValue(hashString, out exportId))
            {
                exportId = _nextExportId++;
                _hashToExportId[hashString] = exportId;
            }

            _manifestRows.Add(new ManifestRow
            {
                MRN = mrn,
                StudyUID = studyUid,
                SeriesUID = seriesUid,
                ExportID = exportId
            });

            return exportId;
        }

        /// <summary>
        /// Returns all manifest rows accumulated during export.
        /// </summary>
        public List<ManifestRow> GetManifestRows()
        {
            return _manifestRows;
        }
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
    }
}
