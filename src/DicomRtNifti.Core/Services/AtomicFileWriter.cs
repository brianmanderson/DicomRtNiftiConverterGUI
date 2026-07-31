using System;
using System.IO;
using System.Text;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Crash-safe replacement for <see cref="File.WriteAllText(string, string)"/> for the small
    /// JSON state files the toolkit persists (the anonymization key, settings, ROI associations).
    ///
    /// <c>File.WriteAllText</c> opens the destination with <c>FileMode.Create</c>, which truncates
    /// it *before* the new bytes are written. A crash, a full disk, or a kill signal anywhere in
    /// that window leaves a half-written or empty file on disk. For the anonymization key that is
    /// not a cosmetic problem: the truncated file no longer parses, every previously assigned
    /// pseudonym is gone, and the next export re-hashes the same patients under new identifiers.
    ///
    /// Writing to a sibling temp file and then replacing the destination in one filesystem
    /// operation means the destination is only ever the complete old content or the complete new
    /// content. The temp file is deliberately created in the *same directory* as the destination:
    /// <see cref="File.Replace(string,string,string)"/> and an atomic rename only work within a
    /// single volume, and the system temp directory routinely lives on another one.
    /// </summary>
    internal static class AtomicFileWriter
    {
        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> as UTF-8 without a BOM
        /// (matching <see cref="File.WriteAllText(string, string)"/>), creating the containing
        /// directory if needed. On return the destination holds the complete new content; if the
        /// call throws, the destination still holds the complete previous content.
        /// </summary>
        public static void WriteAllText(string path, string contents)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path must not be empty.", "path");

            string fullPath = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Guid-suffixed so two processes writing the same file cannot collide on the temp name.
            string temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    // Push the bytes past the OS cache before the rename, so a power loss cannot
                    // leave the rename durable but the content not.
                    stream.Flush(true);
                }

                if (File.Exists(fullPath))
                {
                    // Replace preserves the destination's identity (ACLs, hard links) and is a
                    // single transaction on NTFS. ignoreMetadataErrors keeps an ACL/timestamp copy
                    // failure from aborting a write whose payload is already safely on disk.
                    File.Replace(temp, fullPath, null, true);
                }
                else
                {
                    File.Move(temp, fullPath);
                }
            }
            finally
            {
                // Reached only when the replace never happened (the write threw). Removing the
                // orphan keeps a failed save from littering the directory; failing to remove it is
                // not itself an error worth surfacing.
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
