using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The series inspector's mapping from a scanned <see cref="DicomSeriesGroup"/> to what it
    /// displays. Pure: every model is built in memory, nothing is read from disk, no window opens.
    /// </summary>
    public class SeriesInspectorMappingTests
    {
        private static DicomSeriesGroup Ct(params double[] zs) => new DicomSeriesGroup
        {
            SeriesInstanceUID = "1.2.826.0.1.3680043.2.1",
            SeriesDescription = "Planning CT",
            Modality = "CT",
            FrameOfReferenceUID = "1.2.826.0.1.3680043.2.9",
            FilePaths = zs.Select((_, i) => $"ct_{i:D3}.dcm").ToList(),
            SlicePositions = zs.ToList(),
            PixelSpacing = new[] { 0.977, 0.977 },
            SliceThickness = 2.5,
        };

        private static DicomSeriesGroup RtObject(string modality, string description, RtLinkMatchRule rule, params string[] rois) =>
            new DicomSeriesGroup
            {
                Modality = modality,
                SeriesDescription = description,
                FilePaths = new List<string> { description + ".dcm" },
                RoiNames = rois.ToList(),
                LinkMatchRule = rule,
            };

        [Fact]
        public void A_regular_grid_maps_spacing_extent_and_frame_and_raises_no_badge()
        {
            var vm = new SeriesInspectorViewModel(Ct(-5, -2.5, 0, 2.5, 5));

            Assert.True(vm.HasGeometry);
            Assert.Equal("CT", vm.Modality);
            Assert.Equal("Planning CT", vm.Title);
            Assert.Equal(5, vm.InstanceCount);
            Assert.Equal("2.5 mm", vm.SliceSpacingText);
            Assert.Equal("2.5 mm", vm.DistinctGapsText);
            Assert.Equal("10 mm (z -5 to 5)", vm.ExtentText);
            Assert.Equal("0.977 × 0.977 mm", vm.PixelSpacingText);
            Assert.Equal("1.2.826.0.1.3680043.2.9", vm.FrameOfReferenceUid);
            Assert.False(vm.IsNonUniformZ);
            Assert.Equal("", vm.NonUniformZWarning);
            Assert.False(vm.GapsWithinTolerance);
            Assert.False(vm.HasMissingPositions);
            Assert.Equal("none", vm.RtStructLinkText);
            Assert.Equal("none", vm.RtDoseLinkText);
            Assert.Empty(vm.LinkedObjects);
            Assert.False(vm.HasGuessedLink);
        }

        [Fact]
        public void Mixed_gaps_raise_the_badge_with_the_conversion_warning_verbatim()
        {
            var model = Ct(0, 1, 2, 5);
            var vm = new SeriesInspectorViewModel(model);
            var row = new SeriesGroupViewModel(model);

            Assert.True(vm.IsNonUniformZ);
            Assert.Equal("1, 3 mm", vm.DistinctGapsText);
            Assert.Equal("1 mm", vm.SliceSpacingText); // the median gap, not the 5/3 mm a regular-grid reader reports
            Assert.Equal("5 mm (z 0 to 5)", vm.ExtentText);

            // The badge is the conversions' own test: what Convert Selected and Export Manifest
            // Only log for this series is exactly what the inspector and the tree row show.
            string expected;
            Assert.True(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(model, out expected));
            Assert.Equal(expected, vm.NonUniformZWarning);
            Assert.True(row.IsNonUniformZ);
            Assert.Equal(expected, row.NonUniformZWarning);
        }

        [Fact]
        public void Gaps_that_differ_only_by_rounding_are_listed_without_a_badge()
        {
            // A 0.625 mm grid written to two decimals: non-uniform by the probe's absolute test,
            // but flattening it moves a volume by under 1%, so no conversion warns.
            var vm = new SeriesInspectorViewModel(Ct(0, 0.62, 1.25, 1.87, 2.5));

            Assert.False(vm.IsNonUniformZ);
            Assert.True(vm.GapsWithinTolerance);
            Assert.Equal("0.62, 0.63 mm", vm.DistinctGapsText);
        }

        [Fact]
        public void A_series_without_positions_shows_dashes_rather_than_zeros()
        {
            var vm = new SeriesInspectorViewModel(new DicomSeriesGroup
            {
                Modality = "MR",
                FilePaths = new List<string> { "a.dcm" },
            });

            Assert.False(vm.HasGeometry);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.SliceSpacingText);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.DistinctGapsText);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.ExtentText);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.PixelSpacingText);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.FrameOfReferenceUid);
            Assert.Equal(SeriesInspectorViewModel.Missing, vm.SeriesInstanceUid);
            Assert.Equal("(no series description)", vm.Title);
            Assert.False(vm.IsNonUniformZ);
        }

        [Fact]
        public void Unpositioned_slices_carry_the_conversions_missing_position_warning()
        {
            var model = Ct(0, 1, 2);
            model.UnpositionedSlices.Add("ct_009.dcm (InstanceNumber 10): ImagePositionPatient missing");

            var vm = new SeriesInspectorViewModel(model);

            string expected;
            Assert.True(SeriesGeometryProbe.TryBuildMissingPositionWarning(model, out expected));
            Assert.True(vm.HasMissingPositions);
            Assert.Equal(1, vm.UnpositionedSliceCount);
            Assert.Equal(expected, vm.MissingPositionWarning);
        }

        [Fact]
        public void Link_rules_and_counts_map_to_the_words_the_help_uses()
        {
            var model = Ct(0, 2.5, 5);
            var structureSet = RtObject("RTSTRUCT", "Plan contours", RtLinkMatchRule.ReferencedSeriesUid, "PTV", "Lung_L");
            var dose = RtObject("RTDOSE", "Plan dose", RtLinkMatchRule.LargestSeriesFallback);
            model.LinkedRtStructs.Add(structureSet);
            model.LinkedRtStruct = structureSet;
            model.LinkedRtDoses.Add(dose);
            model.LinkedRtDose = dose;

            var vm = new SeriesInspectorViewModel(model);

            Assert.Equal(1, vm.LinkedRtStructCount);
            Assert.Equal(1, vm.LinkedRtDoseCount);
            Assert.Equal("1 linked, by referenced series UID", vm.RtStructLinkText);
            Assert.Equal("1 linked, by largest-series fallback (a guess)", vm.RtDoseLinkText);
            Assert.True(vm.HasGuessedLink);
            Assert.True(new SeriesGroupViewModel(model).HasGuessedLink);

            Assert.Collection(vm.LinkedObjects,
                s =>
                {
                    Assert.Equal("RTSTRUCT", s.Kind);
                    Assert.Equal("Plan contours", s.Description);
                    Assert.Equal("2 ROI(s)", s.Detail);
                    Assert.Equal("referenced series UID", s.Rule);
                    Assert.False(s.IsGuess);
                },
                d =>
                {
                    Assert.Equal("RTDOSE", d.Kind);
                    Assert.Equal("1 file(s)", d.Detail);
                    Assert.True(d.IsGuess);
                });
        }

        [Fact]
        public void Structure_sets_linked_by_different_rules_are_counted_per_rule()
        {
            var model = Ct(0, 2.5, 5);
            model.LinkedRtStructs.Add(RtObject("RTSTRUCT", "Planning", RtLinkMatchRule.FrameOfReferenceUid));
            model.LinkedRtStructs.Add(RtObject("RTSTRUCT", "Re-contour", RtLinkMatchRule.ReferencedSeriesUid));

            var vm = new SeriesInspectorViewModel(model);

            Assert.Equal(2, vm.LinkedRtStructCount);
            Assert.Equal("2 linked: 1 by referenced series UID, 1 by frame of reference UID", vm.RtStructLinkText);
            Assert.False(vm.HasGuessedLink);
        }

        [Fact]
        public void Tag_rows_follow_catalog_order_and_names_and_skip_absent_tags()
        {
            var keywords = new[] { "PatientID", "Manufacturer", "PixelSpacing", "Rows", "SliceThickness", "StudyDescription", "NoSuchKeyword" };
            var values = new Dictionary<string, object>
            {
                ["PatientID"] = "SYN001",
                ["Manufacturer"] = null,
                ["PixelSpacing"] = new[] { 0.5, 0.5 },
                ["Rows"] = 512L,
                ["SliceThickness"] = 2.5,
                ["StudyDescription"] = "   ",
                ["NoSuchKeyword"] = null,
            };

            var previous = CultureInfo.CurrentCulture;
            try
            {
                // A decimal-comma culture must not leak into the grid.
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var rows = SeriesInspectorViewModel.BuildTagRows("Image", keywords, values);

                Assert.Equal(new[] { "PatientID", "PixelSpacing", "Rows", "SliceThickness" }, rows.Select(r => r.Keyword));
                Assert.All(rows, r => Assert.Equal("Image", r.Section));
                Assert.Equal("Patient ID", rows[0].Attribute);
                Assert.Equal(MetadataTagCatalog.FriendlyName("PixelSpacing"), rows[1].Attribute);
                Assert.Equal(new[] { "SYN001", "0.5\\0.5", "512", "2.5" }, rows.Select(r => r.Value));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Every_curated_keyword_resolves_to_a_friendly_name()
        {
            // The grid labels rows exactly as the metadata.json sidecar keys them.
            foreach (var keyword in MetadataTagCatalog.CuratedImageKeywords
                         .Concat(MetadataTagCatalog.CuratedStructureKeywords)
                         .Concat(MetadataTagCatalog.CuratedDoseKeywords))
            {
                var rows = SeriesInspectorViewModel.BuildTagRows("s", new[] { keyword }, new Dictionary<string, object> { [keyword] = "x" });
                Assert.Equal(MetadataTagCatalog.FriendlyName(keyword), Assert.Single(rows).Attribute);
            }
        }
    }
}
