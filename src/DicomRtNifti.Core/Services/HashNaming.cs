using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Deterministic, content-stable filename helpers for the NIfTI -> DICOM workflow.
    /// Used in server mode so that re-running on the same input filenames produces the
    /// same output filename, allowing skip-if-exists idempotency.
    /// </summary>
    public static class HashNaming
    {
        /// <summary>
        /// Number of hex characters of the SHA256 digest to retain.
        /// 12 chars = 48 bits = ~2.8e14 combinations — collision-free in practice for a single patient folder.
        /// </summary>
        private const int HashCharLength = 12;

        /// <summary>
        /// Produces a stable hash from a list of basenames. Names are normalized to
        /// lowercase invariant culture, sorted ordinally, and joined with '|' before hashing.
        /// Returns the first <see cref="HashCharLength"/> hex characters of the SHA256 digest.
        /// </summary>
        public static string ComputeStableHash(IEnumerable<string> basenames)
        {
            if (basenames == null) return new string('0', HashCharLength);

            var normalized = basenames
                .Select(s => (s ?? string.Empty).ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            string joined = string.Join("|", normalized);
            byte[] bytes = Encoding.UTF8.GetBytes(joined);

            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var sb = new StringBuilder(HashCharLength);
                int byteCount = (HashCharLength + 1) / 2;
                for (int i = 0; i < byteCount; i++)
                    sb.AppendFormat("{0:x2}", digest[i]);
                if (sb.Length > HashCharLength) sb.Length = HashCharLength;
                return sb.ToString();
            }
        }

        /// <summary>
        /// Returns the stable RT-STRUCT filename for the given mask basenames.
        /// </summary>
        public static string RtStructFileName(IEnumerable<string> maskBasenames)
        {
            return $"RTSTRUCT_{ComputeStableHash(maskBasenames)}.dcm";
        }

        /// <summary>
        /// Returns the stable RT-DOSE filename for a single dose basename.
        /// </summary>
        public static string RtDoseFileName(string doseBasename)
        {
            return $"RTDOSE_{ComputeStableHash(new[] { doseBasename })}.dcm";
        }
    }
}
