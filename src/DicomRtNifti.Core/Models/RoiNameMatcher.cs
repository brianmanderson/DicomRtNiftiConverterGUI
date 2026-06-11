using System;
using System.Collections.Generic;
using System.Text;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// Central matching for ROI names against ROI-association aliases / canonical names. Matching is
    /// lightly forgiving: an exact case-insensitive match wins first, then a "normalized" comparison
    /// that treats the separator characters '-', '_' and whitespace as interchangeable (each collapses
    /// to a single space) and ignores case. This covers the separator/case variants TPS vendors emit
    /// for the SAME tokenization — e.g. "Spinal-Cord", "Spinal_Cord", and "Spinal Cord" all match.
    /// It deliberately does NOT delete separators, so a run-together name does NOT match a separated
    /// one ("SpinalCord" does not match "Spinal-Cord"), and it never bridges genuinely different
    /// tokens ("Lung_L" vs "Lung-Left"). Used by both the conversion service (mask renaming) and the
    /// view-models (selection / manifest) so every surface resolves names the same way.
    /// </summary>
    public static class RoiNameMatcher
    {
        /// <summary>
        /// Lowercases and collapses every run of separator characters ('-', '_', or whitespace) into a
        /// single space, trimming leading/trailing separators. The token boundaries are preserved (a
        /// separator is never removed outright), so "Spinal-Cord" and "Spinal Cord" normalize equal but
        /// "SpinalCord" does not. Returns "" for null/empty input.
        /// </summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool pendingSeparator = false;
            foreach (char c in name)
            {
                if (c == '-' || c == '_' || char.IsWhiteSpace(c))
                {
                    // Defer emitting a space until we know a non-separator follows, so leading and
                    // trailing separators (and runs) collapse to nothing / a single space.
                    if (sb.Length > 0) pendingSeparator = true;
                }
                else
                {
                    if (pendingSeparator) { sb.Append(' '); pendingSeparator = false; }
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// True when <paramref name="dicomName"/> matches <paramref name="candidate"/> (an alias or a
        /// canonical name): exact case-insensitive first, then normalized equality.
        /// </summary>
        public static bool Matches(string dicomName, string candidate)
        {
            if (string.Equals(dicomName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
            string a = Normalize(dicomName);
            return a.Length > 0 && a == Normalize(candidate);
        }

        /// <summary>
        /// Resolves a raw DICOM ROI name to its canonical name using the given associations, or returns
        /// the raw name unchanged when nothing matches. The first association (in order) whose canonical
        /// name or any alias matches wins.
        /// </summary>
        public static string ResolveToCanonical(string dicomName, IEnumerable<RoiAssociation> associations)
        {
            if (associations == null) return dicomName;
            foreach (var assoc in associations)
            {
                if (assoc == null) continue;
                if (Matches(dicomName, assoc.CanonicalName))
                    return assoc.CanonicalName;
                if (assoc.Aliases != null)
                    foreach (var alias in assoc.Aliases)
                        if (Matches(dicomName, alias))
                            return assoc.CanonicalName;
            }
            return dicomName;
        }
    }
}
