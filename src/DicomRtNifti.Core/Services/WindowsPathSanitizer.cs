using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Turns an arbitrary string into a single path segment (file or folder name) that is valid on
    /// Windows. Exports may be produced on Linux/macOS but are routinely opened on Windows, so the
    /// rules are applied for every host OS rather than relying on the OS-dependent
    /// <see cref="Path.GetInvalidFileNameChars"/> (which on Linux only reports '/' and NUL).
    ///
    /// Guards: the Windows-forbidden characters and control characters, reserved device names
    /// (CON, PRN, AUX, NUL, COM1-9, LPT1-9), and trailing dots/spaces (which Windows silently trims).
    /// </summary>
    public static class WindowsPathSanitizer
    {
        // Characters Windows disallows in a file/folder name, independent of host OS.
        private static readonly HashSet<char> ForbiddenChars = new HashSet<char>(
            new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' });

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Returns a Windows-safe version of <paramref name="name"/>. Returns
        /// <paramref name="fallback"/> if the input is null/empty or sanitizes to nothing.
        /// </summary>
        public static string SanitizeName(string name, string fallback = "_")
        {
            if (string.IsNullOrEmpty(name))
                return fallback;

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c < 0x20 || ForbiddenChars.Contains(c))
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            // Windows trims trailing dots and spaces from names; trim them ourselves so the name we
            // ask for is the name we get.
            string result = sb.ToString().TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(result))
                return fallback;

            // Reserved device names are invalid even with an extension (e.g. "CON.nii.gz"), so test
            // the portion before the first dot.
            int dot = result.IndexOf('.');
            string baseName = dot >= 0 ? result.Substring(0, dot) : result;
            if (ReservedNames.Contains(baseName))
                result = "_" + result;

            return result;
        }
    }
}
