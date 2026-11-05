using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// <see cref="RoiSelection.Resolve"/> is the desktop app's "Select ROIs" moved into Core (W4-C),
    /// so W3-D.2 can drop the copy in MainViewModel and the web can call the same rule. These pin it
    /// against the GUI's own code, copied below as the oracle, for three selections and the two
    /// no-selection states; and pin what each exports from one structure set, so the table in the
    /// W4-C PR is checked, not just written.
    ///
    /// The GUI hands its selection over only while "Only export specific ROIs" is ticked; Core
    /// applies a selection whenever one is given. So the mapping a front-end uses is
    /// RoiSelection = ticked ? selected : empty, OnlyAssociatedRois = ticked.
    /// </summary>
    public class RoiSelectionTests
    {
        /// <summary>The association list every case uses, in this order.</summary>
        private static List<RoiAssociation> Associations() => new List<RoiAssociation>
        {
            new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "lung left", "LLung" } },
            new RoiAssociation { CanonicalName = "Heart", Aliases = { "heart", "Cor" } },
            new RoiAssociation { CanonicalName = "SpinalCord", Aliases = { "Spinal-Cord", "cord" } },
        };

        /// <summary>The structure set's ROI names, in StructureSetROISequence order.</summary>
        private static readonly List<string> StructureSetRois = new List<string>
        {
            "LLung", "Heart", "Spinal_Cord", "PTV", "Esophagus", "Body",
        };

        public static IEnumerable<object[]> Cases() => new[]
        {
            // id, ticked, selection (in the order the user ticked), effective associations, export unmatched, masks
            new object[] { "1 one associated name", true, new[] { "Heart" },
                "Heart[heart|Cor]", false,
                "Heart<-Heart" },
            new object[] { "2 an associated and an unassociated name", true, new[] { "PTV", "Lung_L" },
                "Lung_L[lung left|LLung]; PTV[PTV]", false,
                "Lung_L<-LLung; PTV<-PTV" },
            new object[] { "3 another case, a new name, an absent name", true, new[] { "spinalcord", "Esophagus", "Bladder" },
                "SpinalCord[Spinal-Cord|cord]; Esophagus[Esophagus]; Bladder[Bladder]", false,
                "Esophagus<-Esophagus; SpinalCord<-Spinal_Cord" },
            new object[] { "4 ticked, nothing selected", true, new string[0],
                "Lung_L[lung left|LLung]; Heart[heart|Cor]; SpinalCord[Spinal-Cord|cord]", false,
                "Heart<-Heart; Lung_L<-LLung; SpinalCord<-Spinal_Cord" },
            new object[] { "5 not ticked, a stale selection", false, new[] { "Heart" },
                "Lung_L[lung left|LLung]; Heart[heart|Cor]; SpinalCord[Spinal-Cord|cord]", true,
                "Body<-Body; Esophagus<-Esophagus; Heart<-Heart; Lung_L<-LLung; PTV<-PTV; SpinalCord<-Spinal_Cord" },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void Resolve_GivesWhatTheGuiGave(
            string id, bool ticked, string[] selection, string associations, bool exportUnmatched, string masks)
        {
            var gui = GuiEffectiveAssociations(Associations(), new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase), ticked);
            var core = RoiSelection.Resolve(Associations(), ticked ? selection : new string[0], onlyAssociatedRois: ticked);

            Assert.True(Describe(gui.Associations) == Describe(core.Associations), id);
            Assert.Equal(gui.ExportUnmatched, core.ExportUnmatched);
            Assert.Equal(Masks(gui.Associations, gui.ExportUnmatched), Masks(core.Associations, core.ExportUnmatched));

            // And what both give, written out, so the PR's table is this test's data.
            Assert.Equal(associations, Describe(core.Associations));
            Assert.Equal(exportUnmatched, core.ExportUnmatched);
            Assert.Equal(masks, Masks(core.Associations, core.ExportUnmatched));
        }

        [Fact]
        public void NoSelection_IsTheAssociationsAsGiven_TheVeryList()
        {
            var associations = Associations();

            var only = RoiSelection.Resolve(associations, null, onlyAssociatedRois: true);
            var every = RoiSelection.Resolve(associations, new List<string>(), onlyAssociatedRois: false);
            var nullAssociations = RoiSelection.Resolve(null, new[] { "", null }, onlyAssociatedRois: false);

            Assert.Same(associations, only.Associations);
            Assert.False(only.ExportUnmatched);
            Assert.False(only.FromSelection);
            Assert.Same(associations, every.Associations);
            Assert.True(every.ExportUnmatched);
            Assert.Null(nullAssociations.Associations);
            Assert.True(nullAssociations.ExportUnmatched);
            Assert.False(nullAssociations.FromSelection);
        }

        [Fact]
        public void ASelection_DropsUnmatchedRois_WhateverOnlyAssociatedRoisSays()
        {
            var on = RoiSelection.Resolve(Associations(), new[] { "Heart" }, onlyAssociatedRois: true);
            var off = RoiSelection.Resolve(Associations(), new[] { "Heart" }, onlyAssociatedRois: false);

            Assert.False(on.ExportUnmatched);
            Assert.False(off.ExportUnmatched);
            Assert.True(off.FromSelection);
            Assert.Equal(Describe(on.Associations), Describe(off.Associations));
        }

        [Fact]
        public void KeptAssociations_StayInTheirOwnOrder_ThenTheSelfMatchingNamesInSelectionOrder()
        {
            var rois = RoiSelection.Resolve(Associations(), new[] { "Zeta", "Heart", "Alpha", "Lung_L" }, false);

            Assert.Equal("Lung_L[lung left|LLung]; Heart[heart|Cor]; Zeta[Zeta]; Alpha[Alpha]", Describe(rois.Associations));
        }

        [Fact]
        public void ANameRepeatedInAnotherCase_CountsOnce_AsItsFirstSpelling()
        {
            var rois = RoiSelection.Resolve(null, new[] { "Ptv", "PTV", "ptv", "", null }, false);

            Assert.Equal("Ptv[Ptv]", Describe(rois.Associations));
        }

        [Fact]
        public void WithNoAssociations_EverySelectedNameMatchesItself_Forgivingly()
        {
            var rois = RoiSelection.Resolve(null, new[] { "Spinal Cord", "Heart" }, false);

            Assert.Equal("Heart<-Heart; Spinal Cord<-Spinal_Cord", Masks(rois.Associations, rois.ExportUnmatched));
        }

        [Fact]
        public void NullAssociationEntries_AreSkipped()
        {
            var associations = Associations();
            associations.Insert(1, null);
            associations.Add(new RoiAssociation { CanonicalName = null });

            var rois = RoiSelection.Resolve(associations, new[] { "Heart" }, false);

            Assert.Equal("Heart[heart|Cor]", Describe(rois.Associations));
        }

        [Fact]
        public void ResolveRois_ReadsTheThreeOptions()
        {
            var options = new CohortExportOptions
            {
                Associations = Associations(),
                OnlyAssociatedRois = false,
                RoiSelection = { "PTV" },
            };

            var rois = CohortExportService.ResolveRois(options);

            Assert.Equal("PTV[PTV]", Describe(rois.Associations));
            Assert.False(rois.ExportUnmatched);
            Assert.Throws<ArgumentNullException>(() => CohortExportService.ResolveRois(null));
        }

        /// <summary>
        /// MainViewModel.cs:558-580 at toolkit main fb841dc (ExecuteConvertAsync; the same block is at
        /// :829-849 in ExecuteExportMetaDataAsync), copied with only the fields turned into
        /// parameters and the result returned. The oracle the GUI's current behaviour is pinned by.
        /// </summary>
        private static (List<RoiAssociation> Associations, bool ExportUnmatched) GuiEffectiveAssociations(
            List<RoiAssociation> _associations, HashSet<string> _selectedRoiNames, bool OnlyExportSpecificRois)
        {
            var effectiveAssociations = _associations;
            bool effectiveExportUnmatched = !OnlyExportSpecificRois;

            if (OnlyExportSpecificRois && _selectedRoiNames != null && _selectedRoiNames.Count > 0)
            {
                var filtered = new List<RoiAssociation>();
                var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var assoc in _associations)
                {
                    if (_selectedRoiNames.Contains(assoc.CanonicalName))
                    {
                        filtered.Add(assoc);
                        coveredNames.Add(assoc.CanonicalName);
                    }
                }
                foreach (var name in _selectedRoiNames)
                {
                    if (!coveredNames.Contains(name))
                        filtered.Add(new RoiAssociation { CanonicalName = name, Aliases = new List<string> { name } });
                }
                effectiveAssociations = filtered;
                effectiveExportUnmatched = false;
            }

            return (effectiveAssociations, effectiveExportUnmatched);
        }

        private static string Describe(List<RoiAssociation> associations) =>
            associations == null
                ? "(null)"
                : string.Join("; ", associations.Select(a => a.CanonicalName + "[" + string.Join("|", a.Aliases) + "]"));

        /// <summary>
        /// The masks the conversion writes for <see cref="StructureSetRois"/>, as mask name &lt;- DICOM
        /// ROI name, in the order the run records them, through the resolver the conversion uses.
        /// </summary>
        private static string Masks(List<RoiAssociation> associations, bool exportUnmatched) =>
            string.Join("; ", NiftiConversionService.ResolveRoiNames(StructureSetRois, associations, exportUnmatched)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "<-" + kv.Value));
    }
}
