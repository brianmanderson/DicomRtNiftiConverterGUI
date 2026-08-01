using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Chooses the file names a reverse run stages its input masks under.
    ///
    /// Both <c>--reverse</c> forms copy the caller's masks into
    /// <c>%TEMP%\rt_mask_validation_stage_&lt;random&gt;\masks\</c> before handing the folder to
    /// <see cref="RtStructWriterService"/>, so it is the *staged* path — not the user's — that has
    /// to fit MAX_PATH. With a default %TEMP% that left roughly 173 characters of basename, and a
    /// mask over it was dropped: SimpleITK failed to read the staged path, the error named a
    /// temporary directory the caller never chose, the ROI was missing from the RTSTRUCT and the
    /// run still exited 0. It also fired before the writer's 64-character ROIName truncation could
    /// help, so the earlier fix for over-long names never got a chance.
    ///
    /// Shortening at staging time closes it, and costs nothing: ROIName (VR LO) is capped at
    /// <see cref="RtStructWriterService.RoiNameMaxLength"/> characters, so a mask staged under its
    /// first 64 characters produces exactly the ROI name the full path would have produced. Names
    /// that collide once clipped are disambiguated here rather than becoming two ROIs with one
    /// name.
    /// </summary>
    internal static class MaskStagingNames
    {
        /// <summary>
        /// Longest full path the staging round-trip may produce. MAX_PATH is 260 including the
        /// terminating NUL, so 259 characters is the last length that works; measured exactly
        /// (259 OK, 260 fails). Applied on every OS: the limit that bites is Windows', the
        /// resulting name is the same 64 characters the RTSTRUCT will carry anyway, and a rule
        /// that changes with the host would make a dropped ROI reproduce on one machine and not
        /// another — which is how this survived as long as it did.
        /// </summary>
        internal const int MaxStagedPathLength = 259;

        /// <summary>
        /// Below this, a clipped name is no longer recognisable enough to be worth writing, and
        /// the staging root is so deep that the caller has to fix it.
        /// </summary>
        private const int MinStagedBaseNameLength = 8;

        /// <summary>
        /// Maps each source mask path to the file name it should be staged under in
        /// <paramref name="stagedMasksDir"/>. Names that already fit are returned unchanged, so
        /// the ordinary case is a straight copy.
        /// </summary>
        /// <param name="notices">
        /// Optional; receives one human-readable line per mask that had to be renamed, naming the
        /// caller's own file rather than the staging path.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The staging directory is so long that even a clipped name cannot fit. Loud on purpose:
        /// the alternative is the silent partial output this type exists to remove.
        /// </exception>
        public static Dictionary<string, string> BuildStagedFileNames(
            string stagedMasksDir,
            IEnumerable<string> sourcePaths,
            List<string> notices = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sourcePaths == null)
                return result;

            // The separator between the directory and the file name counts too.
            int prefixLength = (stagedMasksDir ?? "").Length + 1;
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sourcePaths.OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(source) || result.ContainsKey(source))
                    continue;

                string fileName = Path.GetFileName(source);
                string baseName = NiftiFileNaming.StripNiftiExtension(fileName);
                string extension = fileName.Substring(baseName.Length);

                int budget = MaxStagedPathLength - prefixLength - extension.Length;
                int cap = Math.Min(RtStructWriterService.RoiNameMaxLength, budget);

                if (cap < MinStagedBaseNameLength)
                {
                    throw new InvalidOperationException(BuildStagingTooDeepMessage(
                        source, stagedMasksDir, cap));
                }

                string staged = Claim(baseName, cap, claimed);
                result[source] = staged + extension;

                if (!string.Equals(staged, baseName, StringComparison.Ordinal))
                {
                    notices?.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "Mask '{0}' has a {1}-character name; it is staged (and stored in the " +
                        "RTSTRUCT) as '{2}' so the whole run is not lost to a path-length limit. " +
                        "ROIName is capped at {3} characters regardless.",
                        source, baseName.Length, staged, RtStructWriterService.RoiNameMaxLength));
                }
            }

            return result;
        }

        /// <summary>
        /// Clips <paramref name="baseName"/> to <paramref name="cap"/> characters and takes the
        /// first form no other mask in this run has taken, replacing the tail — not extending it —
        /// so the result still fits.
        /// </summary>
        private static string Claim(string baseName, int cap, HashSet<string> claimed)
        {
            string clipped = baseName.Length <= cap ? baseName : baseName.Substring(0, cap);
            if (claimed.Add(clipped))
                return clipped;

            for (int suffix = 2; ; suffix++)
            {
                string tail = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                string head = clipped.Length + tail.Length <= cap
                    ? clipped
                    : clipped.Substring(0, Math.Max(0, cap - tail.Length));
                string candidate = head + tail;
                if (claimed.Add(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// The refusal when the staging root leaves no room at all. Names the caller's file and
        /// the directory that is actually too long, because quoting only the temporary path — as
        /// the failure this replaces did — tells the reader nothing they can act on.
        /// </summary>
        internal static string BuildStagingTooDeepMessage(
            string sourcePath, string stagedMasksDir, int remaining)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Cannot stage mask '{0}': the staging directory '{1}' leaves only {2} character(s) " +
                "for a file name, under the {3}-character path limit. Set TMP/TEMP to a shorter " +
                "directory and re-run. Refusing rather than skipping the mask: a reverse run that " +
                "drops an ROI and still exits 0 is indistinguishable from a complete one.",
                sourcePath, stagedMasksDir, Math.Max(0, remaining), MaxStagedPathLength);
        }
    }
}
