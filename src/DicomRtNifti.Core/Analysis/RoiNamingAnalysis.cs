using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// ROI names across a cohort: how common each is, which spellings
    /// <see cref="RoiNameMatcher.Normalize"/> would merge, how many patients each canonical name
    /// covers under an association set, and what --only-associated-rois would drop.
    ///
    /// It counts what the desktop app counts, so the two agree name for name: each image series
    /// contributes its default structure set (<see cref="DicomSeriesGroup.LinkedRtStruct"/>, the
    /// one an export takes when nothing chooses another), names compare ignoring case and keep
    /// the first spelling met (MainViewModel's ROI browser counts), and coverage is Select ROIs'
    /// patient count (RoiSelectionViewModel). What would be dropped comes from the export's own
    /// resolver (<see cref="NiftiConversionService.ResolveRoiNames"/>), not a re-implementation.
    /// </summary>
    public static class RoiNamingAnalysis
    {
        /// <param name="scan">The scanned cohort.</param>
        /// <param name="associations">The association set; null or empty for none.</param>
        /// <param name="presentPatient">How a PatientID is shown (hashed under anonymization).</param>
        public static RoiNamingSummary Analyze(
            DicomScanResult scan, IList<RoiAssociation> associations, Func<string, string> presentPatient)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            var assoc = (associations ?? new List<RoiAssociation>()).Where(a => a != null).ToList();
            presentPatient = presentPatient ?? (id => id);
            bool haveAssociations = assoc.Count > 0;

            var entries = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
            var order = new List<Tally>();
            Tally TallyOf(string name)
            {
                if (!entries.TryGetValue(name, out var tally))
                {
                    tally = new Tally { Name = name };
                    entries[name] = tally;
                    order.Add(tally);
                }
                return tally;
            }

            // The default structure sets, patient by patient, in the tree's order.
            var perPatient = new List<KeyValuePair<string, List<string>>>();
            var dropped = new Dictionary<string, RoiNameCount>(StringComparer.OrdinalIgnoreCase);
            var droppedOrder = new List<RoiNameCount>();
            foreach (var patient in scan.Patients)
            {
                var rois = new List<string>();
                foreach (var study in patient.Studies)
                {
                    foreach (var image in study.Series.Where(SeriesFlags.IsImageSeries))
                    {
                        var set = image.LinkedRtStruct;
                        if (set?.RoiNames == null) continue;
                        rois.AddRange(set.RoiNames);
                        foreach (var name in set.RoiNames)
                        {
                            var tally = TallyOf(name);
                            tally.Series++;
                            tally.Patients.Add(patient.PatientID ?? "");
                        }

                        var kept = new HashSet<string>(
                            NiftiConversionService.ResolveRoiNames(set.RoiNames, assoc, exportUnmatched: false).Values,
                            StringComparer.Ordinal);
                        foreach (var name in set.RoiNames.Where(n => !kept.Contains(n)))
                        {
                            if (!dropped.TryGetValue(name, out var count))
                            {
                                count = new RoiNameCount { Name = name };
                                dropped[name] = count;
                                droppedOrder.Add(count);
                            }
                            count.Series++;
                        }
                    }
                }
                if (rois.Count > 0)
                    perPatient.Add(new KeyValuePair<string, List<string>>(patient.PatientID ?? "", rois));
            }

            // Every structure set in the scan, linked or not, default or not.
            foreach (var set in scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series)
                         .Where(s => s.Modality == "RTSTRUCT"))
            {
                foreach (var name in (set.RoiNames ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    TallyOf(name).StructureSets++;
            }

            var summary = new RoiNamingSummary();
            var sorted = order.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var tally in sorted)
            {
                summary.Names.Add(new RoiNameEntry
                {
                    Name = tally.Name,
                    Series = tally.Series,
                    Patients = tally.Patients.Count,
                    StructureSets = tally.StructureSets,
                    Normalized = RoiNameMatcher.Normalize(tally.Name),
                    CanonicalName = RoiNameMatcher.ResolveToCanonical(tally.Name, assoc),
                    Associated = haveAssociations && !SeriesFlags.IsUnassociated(tally.Name, assoc),
                });
            }

            summary.Clusters = summary.Names
                .Where(n => n.Normalized.Length > 0)
                .GroupBy(n => n.Normalized, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RoiNameCluster
                {
                    Normalized = g.Key,
                    Names = g.Select(n => n.Name).ToList(),
                    Series = g.Sum(n => n.Series),
                })
                .ToList();

            summary.Coverage = Coverage(summary.Names, perPatient, assoc, presentPatient);

            if (haveAssociations)
                summary.Uncovered = summary.Names.Where(n => !n.Associated).Select(n => n.Name).ToList();

            summary.DroppedIfOnlyAssociated = droppedOrder.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return summary;
        }

        /// <summary>
        /// RoiSelectionViewModel's counts: the canonical names the discovered names resolve to, and
        /// for each, the patients (with any ROI) one of whose default-set names resolves to it.
        /// </summary>
        private static RoiCoverage Coverage(
            List<RoiNameEntry> names,
            List<KeyValuePair<string, List<string>>> perPatient,
            List<RoiAssociation> associations,
            Func<string, string> presentPatient)
        {
            var canonical = new List<string>();
            var canonicalSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names.Where(n => n.Series > 0))
            {
                if (canonicalSet.Add(name.CanonicalName))
                    canonical.Add(name.CanonicalName);
            }
            // The canonical list's spelling, so a patient's row joins to it whatever case it met the name in.
            var spelling = canonical.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var coverage = new RoiCoverage { PatientsWithRois = perPatient.Count };
            foreach (var patient in perPatient)
            {
                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in patient.Value)
                    resolved.Add(RoiNameMatcher.ResolveToCanonical(raw, associations));
                foreach (var name in resolved)
                    counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;

                coverage.Patients.Add(new PatientCoverage
                {
                    PatientId = presentPatient(patient.Key),
                    CanonicalNames = resolved
                        .Select(n => spelling.TryGetValue(n, out var s) ? s : n)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                });
            }

            foreach (var name in canonical.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                counts.TryGetValue(name, out int patients);
                coverage.Canonical.Add(new CanonicalCoverage
                {
                    CanonicalName = name,
                    FromAssociation = associations.Any(a => string.Equals(a.CanonicalName, name, StringComparison.OrdinalIgnoreCase)),
                    Patients = patients,
                    Share = perPatient.Count == 0 ? 0 : Math.Round((double)patients / perPatient.Count, 4),
                });
            }
            return coverage;
        }

        private sealed class Tally
        {
            public string Name;
            public int Series;
            public int StructureSets;
            public readonly HashSet<string> Patients = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
