using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Centralizes NIfTI file discovery and naming for the NIfTI -> DICOM pipeline.
    /// SimpleITK transparently loads both gzipped (<c>.nii.gz</c>) and plain (<c>.nii</c>)
    /// files, so the only thing that had to change to support plain <c>.nii</c> inputs
    /// is the file-discovery / extension-stripping layer that previously hard-coded
    /// <c>*.nii.gz</c>. Keeping that logic in one place avoids the four call sites
    /// drifting apart.
    /// </summary>
    internal static class NiftiFileNaming
    {
        public const string ImageNiiGz = "image.nii.gz";
        public const string ImageNii   = "image.nii";

        private const string ExtNiiGz = ".nii.gz";
        private const string ExtNii   = ".nii";

        /// <summary>
        /// Returns every NIfTI file (<c>.nii.gz</c> or plain <c>.nii</c>) directly inside
        /// <paramref name="directory"/>, sorted by file name (ordinal, case-insensitive).
        /// When both <c>foo.nii</c> and <c>foo.nii.gz</c> exist, the gzipped variant
        /// wins and the plain one is dropped, with the dropped path appended to
        /// <paramref name="duplicateWarnings"/> when provided. Returns an empty list
        /// if <paramref name="directory"/> does not exist.
        /// </summary>
        public static IReadOnlyList<string> EnumerateNiftiFiles(
            string directory,
            List<string> duplicateWarnings = null)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return Array.Empty<string>();

            // Enumerate "*.nii*" then post-filter: on Windows a bare "*.nii" pattern
            // can also match "*.nii.gz" because of legacy 8.3 short-name behavior,
            // so explicit EndsWith checks are the safe form.
            var hits = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.nii*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(ExtNiiGz, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(ExtNii,   StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(path);
                }
            }

            if (hits.Count <= 1)
                return hits;

            // De-duplicate: if both foo.nii.gz and foo.nii are present, prefer the
            // gzipped variant so we don't double-convert the same logical volume.
            var byBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in hits)
            {
                string baseName = StripNiftiExtension(Path.GetFileName(path));
                bool isGz = Path.GetFileName(path).EndsWith(ExtNiiGz, StringComparison.OrdinalIgnoreCase);

                if (!byBaseName.TryGetValue(baseName, out string existing))
                {
                    byBaseName[baseName] = path;
                    continue;
                }

                bool existingIsGz = Path.GetFileName(existing).EndsWith(ExtNiiGz, StringComparison.OrdinalIgnoreCase);
                if (isGz && !existingIsGz)
                {
                    duplicateWarnings?.Add(existing);
                    byBaseName[baseName] = path;
                }
                else if (!isGz && existingIsGz)
                {
                    duplicateWarnings?.Add(path);
                }
            }

            return byBaseName.Values
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Strips <c>.nii.gz</c> (preferred) or <c>.nii</c> from a file name,
        /// case-insensitive. Falls back to <see cref="Path.GetFileNameWithoutExtension(string)"/>
        /// when neither extension is present.
        /// </summary>
        public static string StripNiftiExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string name = Path.GetFileName(fileName);
            if (name.EndsWith(ExtNiiGz, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ExtNiiGz.Length);
            if (name.EndsWith(ExtNii, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ExtNii.Length);
            return Path.GetFileNameWithoutExtension(name);
        }

        /// <summary>
        /// Returns true and sets <paramref name="path"/> to <c>folder/image.nii.gz</c>
        /// when it exists, otherwise <c>folder/image.nii</c>. If both exist, the
        /// gzipped variant wins. Returns false (with <paramref name="path"/> = null)
        /// when neither is present.
        /// </summary>
        public static bool TryGetImageNiftiPath(string folder, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(folder)) return false;

            string gz = Path.Combine(folder, ImageNiiGz);
            if (File.Exists(gz)) { path = gz; return true; }

            string nii = Path.Combine(folder, ImageNii);
            if (File.Exists(nii)) { path = nii; return true; }

            return false;
        }
    }
}
