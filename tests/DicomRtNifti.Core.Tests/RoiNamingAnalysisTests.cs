using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// <see cref="RoiNamingAnalysis"/> counts what the desktop app counts. Held against verbatim
    /// copies of MainViewModel's discovered-name counts and of RoiSelectionViewModel's patient
    /// coverage (toolkit a40fd83), and against the export's own resolver for what
    /// --only-associated-rois drops.
    /// </summary>
    public class RoiNamingAnalysisTests
    {
        private static readonly List<RoiAssociation> Associations = new List<RoiAssociation>
        {
            new RoiAssociation { CanonicalName = "SpinalCord", Aliases = { "Spinal-Cord", "cord" } },
            new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "Lung-L", "LLung" } },
        };

        /// <summary>
        /// Three patients: P1 has two series, one with a second (non-default) structure set; P2's
        /// names differ from P1's only in case and separators; P3 has no structure set at all.
        /// </summary>
        private static DicomScanResult Cohort()
        {
            var p1 = Patient("P1",
                Image("1.1", Set("1.1.3", "GTV", "Spinal-Cord", "Lung_L"), Set("1.1.4", "Heart", "GTV")),
                Image("1.2", Set("1.2.3", "gtv", "PTV 70")));
            var p2 = Patient("P2", Image("2.1", Set("2.1.3", "spinal cord", "LLung", "Lung-L")));
            var p3 = Patient("P3", Image("3.1"));
            return new DicomScanResult { Patients = { p1, p2, p3 } };
        }

        [Fact]
        public void Names_AreCountedAsTheRoiBrowserCountsThem()
        {
            var scan = Cohort();
            var summary = RoiNamingAnalysis.Analyze(scan, Associations, id => id);

            var gui = GuiDiscoveredRoiCounts(scan);
            Assert.Equal(
                gui.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                summary.Names.Where(n => n.Series > 0).Select(n => n.Name));
            foreach (var name in summary.Names.Where(n => n.Series > 0))
                Assert.Equal(gui[name.Name], name.Series);

            var gtv = summary.Names.Single(n => n.Name == "GTV");
            Assert.Equal(2, gtv.Series);
            Assert.Equal(1, gtv.Patients);
            Assert.Equal(3, gtv.StructureSets);

            // Only in a second structure set: no series counts it, the structure sets do.
            var heart = summary.Names.Single(n => n.Name == "Heart");
            Assert.Equal(0, heart.Series);
            Assert.Equal(1, heart.StructureSets);
        }

        [Fact]
        public void Coverage_IsSelectRoisPatientCount()
        {
            var scan = Cohort();
            var summary = RoiNamingAnalysis.Analyze(scan, Associations, id => "#" + id);

            var (canonical, counts, total) = GuiCoverage(scan, Associations);
            Assert.Equal(total, summary.Coverage.PatientsWithRois);
            Assert.Equal(2, total);
            Assert.Equal(canonical, summary.Coverage.Canonical.Select(c => c.CanonicalName));
            foreach (var c in summary.Coverage.Canonical)
            {
                counts.TryGetValue(c.CanonicalName, out int expected);
                Assert.Equal(expected, c.Patients);
                Assert.Equal(Math.Round((double)expected / total, 4), c.Share);
            }

            Assert.True(summary.Coverage.Canonical.Single(c => c.CanonicalName == "SpinalCord").FromAssociation);
            Assert.False(summary.Coverage.Canonical.Single(c => c.CanonicalName == "GTV").FromAssociation);
            Assert.Equal(new[] { "#P1", "#P2" }, summary.Coverage.Patients.Select(p => p.PatientId));
            Assert.Equal(new[] { "GTV", "Lung_L", "PTV 70", "SpinalCord" }, summary.Coverage.Patients[0].CanonicalNames);
            Assert.Equal(new[] { "Lung_L", "SpinalCord" }, summary.Coverage.Patients[1].CanonicalNames);
        }

        [Fact]
        public void Clusters_AreTheNamesNormalizeMakesEqual()
        {
            var summary = RoiNamingAnalysis.Analyze(Cohort(), Associations, id => id);

            Assert.Equal(new[] { "lung l", "spinal cord" }, summary.Clusters.Select(c => c.Normalized));
            Assert.Equal(new[] { "Lung-L", "Lung_L" }, summary.Clusters[0].Names);
            Assert.Equal(new[] { "spinal cord", "Spinal-Cord" }, summary.Clusters[1].Names);
            // "GTV" and "gtv" are one name, compared ignoring case; not a cluster.
            Assert.DoesNotContain(summary.Clusters, c => c.Normalized == "gtv");
        }

        [Fact]
        public void Dropped_IsWhatTheExportsResolverDropsUnderOnlyAssociated()
        {
            var scan = Cohort();
            var summary = RoiNamingAnalysis.Analyze(scan, Associations, id => id);

            // The resolver's output, read back as the rasterizer reads it: DICOM names ignoring case.
            var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series).Where(s => s.LinkedRtStruct != null))
            {
                var kept = new HashSet<string>(
                    NiftiConversionService.ResolveRoiNames(image.LinkedRtStruct.RoiNames, Associations, false).Values,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var name in image.LinkedRtStruct.RoiNames.Where(n => !kept.Contains(n)))
                    expected[name] = expected.TryGetValue(name, out int c) ? c + 1 : 1;
            }

            Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => (e.Key, e.Value)),
                summary.DroppedIfOnlyAssociated.Select(d => (d.Name, d.Series)));
            // Both P2 names match Lung_L; the resolver maps the first, so the second is dropped.
            Assert.Contains(summary.DroppedIfOnlyAssociated, d => d.Name == "Lung-L");
            Assert.Equal(new[] { "GTV", "Heart", "PTV 70" }, summary.Uncovered);
        }

        /// <summary>
        /// The resolver keys its map ignoring case, so "GTV" and "gtv" in one structure set leave
        /// one entry; the rasterizer looks DICOM names up ignoring case too, so both still export
        /// (one suffixed with its ROINumber). The ROI table says so, rather than reporting one dropped.
        /// </summary>
        [Fact]
        public void NamesDifferingOnlyInCase_BothExport_AsTheRasterizerReadsTheResolver()
        {
            var image = CohortAnalysisFlagTests.Ct("6.1");
            CohortAnalysisFlagTests.Link(image, Set("6.1.3", "GTV", "gtv", "Body"));
            var study = CohortAnalysisFlagTests.Study(image);
            study.Series.Add(image.LinkedRtStruct);

            var rois = CohortAnalysisFlagTests.Analyze(study).Series.Single().Rois;

            Assert.Equal(new[] { "GTV", "gtv", "Body" }, rois.Select(r => r.Name));
            Assert.Equal(new[] { "GTV", "GTV", "Body" }, rois.Select(r => r.OutputName));
            Assert.All(rois, r => Assert.True(r.KeptIfOnlyAssociated));
        }

        [Fact]
        public void WithoutAssociations_EveryNameStandsForItself_AndNothingIsDropped()
        {
            var summary = RoiNamingAnalysis.Analyze(Cohort(), null, id => id);

            Assert.All(summary.Names, n => Assert.Equal(n.Name, n.CanonicalName));
            Assert.All(summary.Names, n => Assert.False(n.Associated));
            Assert.Empty(summary.Uncovered);
            Assert.Empty(summary.DroppedIfOnlyAssociated);
            Assert.All(summary.Coverage.Canonical, c => Assert.False(c.FromAssociation));
        }

        // ---- Verbatim copies of the GUI's counts, at toolkit a40fd83 --------------------------

        /// <summary>MainViewModel.cs:495-503 (ComputeDiscoveredRoiCounts), over each image series' RoiNames (its LinkedRtStruct's).</summary>
        private static Dictionary<string, int> GuiDiscoveredRoiCounts(DicomScanResult scan)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in scan.Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.Series.Where(SeriesFlags.IsImageSeries))
                        foreach (var roiName in series.LinkedRtStruct?.RoiNames ?? new List<string>())
                            counts[roiName] = counts.TryGetValue(roiName, out int c) ? c + 1 : 1;
            return counts;
        }

        /// <summary>
        /// MainViewModel.cs:477-488 and 994-1004 (discovered names, perPatientRoiNames) and
        /// RoiSelectionViewModel.cs:33-63 (canonical names, patientCounts, totalPatients).
        /// </summary>
        private static (List<string>, Dictionary<string, int>, int) GuiCoverage(DicomScanResult scan, List<RoiAssociation> associations)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var patient in scan.Patients)
                foreach (var study in patient.Studies)
                    foreach (var series in study.Series.Where(SeriesFlags.IsImageSeries))
                        foreach (var roiName in series.LinkedRtStruct?.RoiNames ?? new List<string>())
                            names.Add(roiName);
            var discoveredRoiNames = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            var perPatientRoiNames = new Dictionary<string, List<string>>();
            foreach (var patient in scan.Patients)
            {
                var rois = new List<string>();
                foreach (var study in patient.Studies)
                    foreach (var series in study.Series.Where(SeriesFlags.IsImageSeries))
                        if (series.LinkedRtStruct?.RoiNames != null)
                            rois.AddRange(series.LinkedRtStruct.RoiNames);
                if (rois.Count > 0)
                    perPatientRoiNames[patient.PatientID] = rois;
            }

            var canonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in discoveredRoiNames)
                canonicalNames.Add(RoiNameMatcher.ResolveToCanonical(rawName, associations));

            var patientCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in perPatientRoiNames)
            {
                var resolvedForPatient = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawRoi in kvp.Value)
                    resolvedForPatient.Add(RoiNameMatcher.ResolveToCanonical(rawRoi, associations));
                foreach (var canonical in resolvedForPatient)
                {
                    if (!patientCounts.ContainsKey(canonical))
                        patientCounts[canonical] = 0;
                    patientCounts[canonical]++;
                }
            }

            return (canonicalNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), patientCounts, perPatientRoiNames.Count);
        }

        // ---- In-memory cohort -----------------------------------------------------------------

        private static DicomPatientGroup Patient(string id, params DicomSeriesGroup[] images)
        {
            var study = new DicomStudyGroup { StudyInstanceUID = id + ".s" };
            foreach (var image in images)
            {
                study.Series.Add(image);
                study.Series.AddRange(image.LinkedRtStructs);
            }
            return new DicomPatientGroup { PatientID = id, Studies = { study } };
        }

        private static DicomSeriesGroup Image(string uid, params DicomSeriesGroup[] structureSets)
        {
            var image = new DicomSeriesGroup { SeriesInstanceUID = uid, Modality = "CT" };
            foreach (var set in structureSets)
                CohortAnalysisFlagTests.Link(image, set);
            return image;
        }

        private static DicomSeriesGroup Set(string uid, params string[] names) => new DicomSeriesGroup
        {
            SeriesInstanceUID = uid,
            Modality = "RTSTRUCT",
            RoiNames = names.ToList(),
            LinkMatchRule = RtLinkMatchRule.ReferencedSeriesUid,
        };
    }
}
