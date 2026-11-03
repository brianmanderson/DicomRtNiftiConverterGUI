using System;
using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// What an export does with a structure set's ROIs: the associations their names are matched
    /// against, and whether an ROI that matches none of them is still exported under its own name.
    /// The two arguments <see cref="Services.NiftiConversionService"/> takes for every series.
    /// </summary>
    public sealed class EffectiveRois
    {
        public EffectiveRois(List<RoiAssociation> associations, bool exportUnmatched, bool fromSelection)
        {
            Associations = associations;
            ExportUnmatched = exportUnmatched;
            FromSelection = fromSelection;
        }

        /// <summary>The associations to match ROI names against; may be null or empty.</summary>
        public List<RoiAssociation> Associations { get; }

        /// <summary>True when an ROI that matches no association is exported under its raw name.</summary>
        public bool ExportUnmatched { get; }

        /// <summary>
        /// True when an ROI selection produced these (<see cref="RoiSelection.Resolve"/>); false
        /// when they are the associations as given.
        /// </summary>
        public bool FromSelection { get; }
    }

    /// <summary>
    /// The desktop app's "Select ROIs" as one pure function, so every front-end narrows an export
    /// to a set of canonical names the same way. The rule is the one the GUI's Convert Selected and
    /// Export Manifest Only applied inside the view-model (MainViewModel.cs:561-580 before W3-D.2):
    /// <list type="bullet">
    /// <item>an association whose canonical name is selected is kept, with every alias it has;</item>
    /// <item>a selected name that no association carries matches itself, as an association whose
    /// only alias is that name (and which <see cref="RoiNameMatcher"/> then matches forgivingly);</item>
    /// <item>every ROI that matches none of the above is dropped.</item>
    /// </list>
    /// Names compare case-insensitively, as the GUI's selection set did. The kept associations
    /// stay in their own order, then the self-matching names follow in selection order: the first
    /// association that matches an ROI names its mask, so the order is part of the result.
    /// </summary>
    public static class RoiSelection
    {
        /// <summary>
        /// The associations and unmatched-ROI rule an export applies.
        /// </summary>
        /// <param name="associations">Canonical-name / alias mappings; may be null.</param>
        /// <param name="selectedNames">
        /// Canonical names to keep. Null or empty selects nothing, which leaves the export as
        /// <paramref name="onlyAssociatedRois"/> alone makes it. Null and empty names are ignored;
        /// a name repeated in another case counts once, as the first spelling.
        /// </param>
        /// <param name="onlyAssociatedRois">
        /// With no selection: true drops ROIs that match no association. A selection always drops
        /// them, so this has no effect alongside one.
        /// </param>
        public static EffectiveRois Resolve(
            List<RoiAssociation> associations,
            IEnumerable<string> selectedNames,
            bool onlyAssociatedRois)
        {
            var selected = new List<string>();
            var selectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedNames != null)
            {
                foreach (var name in selectedNames)
                {
                    if (!string.IsNullOrEmpty(name) && selectedSet.Add(name))
                        selected.Add(name);
                }
            }

            // No selection: the associations as given, the very list, so a run without one is the
            // run it always was.
            if (selected.Count == 0)
                return new EffectiveRois(associations, !onlyAssociatedRois, fromSelection: false);

            var effective = new List<RoiAssociation>();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (associations != null)
            {
                foreach (var association in associations)
                {
                    if (association?.CanonicalName != null && selectedSet.Contains(association.CanonicalName))
                    {
                        effective.Add(association);
                        covered.Add(association.CanonicalName);
                    }
                }
            }
            foreach (var name in selected)
            {
                if (!covered.Contains(name))
                    effective.Add(new RoiAssociation { CanonicalName = name, Aliases = new List<string> { name } });
            }

            return new EffectiveRois(effective, exportUnmatched: false, fromSelection: true);
        }
    }
}
