using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The patient and study inspectors' mapping from a scanned <see cref="DicomPatientGroup"/> /
    /// <see cref="DicomStudyGroup"/> to what they display. Pure: every model is built in memory,
    /// nothing is read from disk, no window opens. Links are wired the way the scanner wires them:
    /// the RT object carries its rule, and the image series lists the object it is linked to.
    /// </summary>
    public class PatientStudyInspectorMappingTests
    {
        private const string FrameA = "1.2.826.0.1.3680043.2.9";
        private const string FrameB = "1.2.826.0.1.3680043.2.10";

        private static DicomSeriesGroup Image(string modality, string description, string frame, params double[] zs) => new DicomSeriesGroup
        {
            SeriesInstanceUID = "1.2.826.0.1.3680043.2." + description.Length,
            SeriesDescription = description,
            Modality = modality,
            FrameOfReferenceUID = frame,
            FilePaths = zs.Select((_, i) => $"{description}_{i:D3}.dcm").ToList(),
            SlicePositions = zs.ToList(),
            PixelSpacing = new[] { 1.0, 1.0 },
        };

        private static DicomSeriesGroup RtObject(string modality, string description, params string[] rois) => new DicomSeriesGroup
        {
            Modality = modality,
            SeriesDescription = description,
            FilePaths = new List<string> { description + ".dcm" },
            RoiNames = rois.ToList(),
        };

        private static void LinkStruct(DicomSeriesGroup image, DicomSeriesGroup rtStruct, RtLinkMatchRule rule)
        {
            rtStruct.LinkMatchRule = rule;
            image.LinkedRtStructs.Add(rtStruct);
            if (image.LinkedRtStruct == null)
                image.LinkedRtStruct = rtStruct;
        }

        private static void LinkDose(DicomSeriesGroup image, DicomSeriesGroup rtDose, RtLinkMatchRule rule)
        {
            rtDose.LinkMatchRule = rule;
            image.LinkedRtDoses.Add(rtDose);
            if (image.LinkedRtDose == null)
                image.LinkedRtDose = rtDose;
        }

        private static DicomStudyGroup Study(string date, string description, params DicomSeriesGroup[] series) => new DicomStudyGroup
        {
            StudyInstanceUID = "1.2.826.0.1.3680043.3." + date,
            StudyDate = date,
            StudyDescription = description,
            Series = series.ToList(),
        };

        /// <summary>
        /// Two studies: a planning study (CT with its structure set and dose, a mixed-gap MR, a
        /// plan) and a follow-up (CT and PT, a structure set the scanner could only guess the
        /// series of).
        /// </summary>
        private static DicomPatientGroup TwoStudyPatient()
        {
            var planningCt = Image("CT", "Planning CT", FrameA, 0, 2.5, 5, 7.5, 10);
            var mr = Image("MR", "T1 post", FrameA, 0, 1, 2, 5);
            var planStruct = RtObject("RTSTRUCT", "Plan contours", "PTV", "Lung_L", "Heart");
            var planDose = RtObject("RTDOSE", "Plan dose");
            var plan = RtObject("RTPLAN", "Plan");
            LinkStruct(planningCt, planStruct, RtLinkMatchRule.ReferencedSeriesUid);
            LinkDose(planningCt, planDose, RtLinkMatchRule.FrameOfReferenceUid);

            var followUpCt = Image("CT", "Follow-up CT", FrameB, 0, 3, 6, 9);
            var pet = Image("PT", "PET", FrameB, 0, 3);
            var followUpStruct = RtObject("RTSTRUCT", "Response", "GTV");
            LinkStruct(followUpCt, followUpStruct, RtLinkMatchRule.LargestSeriesFallback);

            return new DicomPatientGroup
            {
                PatientID = "SYN001",
                PatientName = "Doe^Jane",
                Studies = new List<DicomStudyGroup>
                {
                    Study("20240305", "Planning", planningCt, mr, planStruct, planDose, plan),
                    Study("20240401", "Follow-up", followUpCt, pet, followUpStruct),
                },
            };
        }

        [Fact]
        public void A_patient_with_two_studies_and_mixed_modalities_is_summarised_without_expanding_the_tree()
        {
            var vm = new PatientInspectorViewModel(TwoStudyPatient());

            Assert.Equal("Patient inspector", vm.Header);
            Assert.Equal("SYN001", vm.PatientId);
            Assert.Equal("Doe^Jane", vm.PatientName);
            Assert.Equal("SYN001 - Doe^Jane", vm.Title);
            Assert.Equal(2, vm.StudyCount);
            Assert.Equal(4, vm.ImageSeriesCount);
            Assert.Equal("2 CT, 1 MR, 1 PT", vm.ImageSeriesSummary);
            Assert.Equal(5 + 4 + 4 + 2, vm.ImageInstanceCount);
            Assert.Equal(2, vm.LinkedRtStructCount);
            Assert.Equal(1, vm.LinkedRtDoseCount);
            Assert.Equal("2 linked", vm.RtStructText);
            Assert.Equal("1 linked", vm.RtDoseText);
            Assert.Equal("1 RTPLAN", vm.OtherObjectsText);
            Assert.False(vm.HasUnlinkedObjects);

            Assert.Collection(vm.Studies,
                planning =>
                {
                    Assert.Equal("2024-03-05", planning.DateText);
                    Assert.Equal("Planning", planning.Description);
                    Assert.Equal(2, planning.ImageSeriesCount);
                    Assert.Equal(3, planning.RtObjectCount);
                    Assert.Equal("2 image series · 3 RT object(s)", planning.Summary);
                    Assert.False(planning.HasUnlinkedObjects);
                },
                followUp =>
                {
                    Assert.Equal("2024-04-01", followUp.DateText);
                    Assert.Equal(2, followUp.ImageSeriesCount);
                    Assert.Equal(1, followUp.RtObjectCount);
                });
        }

        [Fact]
        public void The_rolled_up_badge_counts_are_the_patient_rows_counts()
        {
            var model = TwoStudyPatient();
            var vm = new PatientInspectorViewModel(model);
            var row = new PatientGroupViewModel(model);

            // One mixed-gap MR, one structure set attached by the largest-series fallback.
            Assert.Equal(1, row.NonUniformSeriesCount);
            Assert.Equal(1, row.GuessedLinkSeriesCount);
            Assert.Equal(row.NonUniformSeriesCount, vm.NonUniformSeriesCount);
            Assert.Equal(row.GuessedLinkSeriesCount, vm.GuessedLinkSeriesCount);
            Assert.Equal(row.NonUniformBadgeText, vm.NonUniformBadgeText);
            Assert.Equal(row.GuessedLinkBadgeText, vm.GuessedLinkBadgeText);

            // A second badged series of each kind changes both the same way.
            var cbct = Image("CT", "CBCT", FrameA, 0, 1, 2, 6);
            LinkDose(cbct, RtObject("RTDOSE", "Guessed dose"), RtLinkMatchRule.LargestSeriesFallback);
            model.Studies[0].Series.Add(cbct);
            vm = new PatientInspectorViewModel(model);
            row = new PatientGroupViewModel(model);
            Assert.Equal(2, row.NonUniformSeriesCount);
            Assert.Equal(2, row.GuessedLinkSeriesCount);
            Assert.Equal(row.NonUniformSeriesCount, vm.NonUniformSeriesCount);
            Assert.Equal(row.GuessedLinkSeriesCount, vm.GuessedLinkSeriesCount);
            Assert.Equal("2 non-uniform Z", vm.NonUniformBadgeText);
            Assert.Equal(row.NonUniformBadgeText, vm.NonUniformBadgeText);
            Assert.Equal("2 links guessed", vm.GuessedLinkBadgeText);
            Assert.Equal(row.GuessedLinkBadgeText, vm.GuessedLinkBadgeText);

            // The study inspector's per-series badges are the series rows' badges.
            var study = new StudyInspectorViewModel(model.Studies[0]);
            var studyRow = new StudyGroupViewModel(model.Studies[0]);
            Assert.Equal(studyRow.ImageSeries.Select(s => s.IsNonUniformZ), study.ImageSeries.Select(s => s.IsNonUniformZ));
            Assert.Equal(studyRow.ImageSeries.Select(s => s.HasGuessedLink), study.ImageSeries.Select(s => s.HasGuessedLink));
            Assert.Equal(studyRow.ImageSeries.Select(s => s.NonUniformZWarning), study.ImageSeries.Select(s => s.NonUniformZWarning));
        }

        [Fact]
        public void A_study_lists_every_object_the_tree_hides_and_badges_the_one_linked_to_nothing()
        {
            var ct = Image("CT", "Planning CT", FrameA, 0, 2.5, 5);
            var linked = RtObject("RTSTRUCT", "Plan contours", "PTV", "Cord");
            var orphan = RtObject("RTSTRUCT", "Old contours", "GTV");
            var plan = RtObject("RTPLAN", "Plan");
            LinkStruct(ct, linked, RtLinkMatchRule.ReferencedSeriesUid);
            var model = Study("20240305", "Planning", ct, linked, orphan, plan);

            var vm = new StudyInspectorViewModel(model);

            Assert.Equal("Study inspector", vm.Header);
            Assert.Equal("Planning", vm.Title);
            Assert.Equal("2024-03-05", vm.StudyDateText);
            Assert.Equal(model.StudyInstanceUID, vm.StudyInstanceUid);
            Assert.Equal("1 CT", vm.ImageSeriesSummary);
            Assert.Equal(3, vm.RtObjectCount);
            Assert.Equal(1, vm.UnlinkedObjectCount);
            Assert.True(vm.HasUnlinkedObjects);
            Assert.Equal("unlinked", vm.UnlinkedBadgeText);
            Assert.Equal(StudyInspectorViewModel.UnlinkedNote, vm.UnlinkedObjectsNote);

            var series = Assert.Single(vm.ImageSeries);
            Assert.Equal("CT", series.Modality);
            Assert.Equal("Planning CT", series.Description);
            Assert.Equal(3, series.InstanceCount);
            Assert.Equal("3 instance(s)", series.Detail);

            Assert.Collection(vm.RtObjects,
                s =>
                {
                    Assert.Equal("RTSTRUCT", s.Kind);
                    Assert.Equal("Plan contours", s.Description);
                    Assert.Equal("2 ROI(s)", s.Detail);
                    Assert.Equal("linked to [CT] Planning CT, by referenced series UID", s.Link);
                    Assert.False(s.IsUnlinked);
                    Assert.False(s.IsGuess);
                },
                o =>
                {
                    Assert.Equal("Old contours", o.Description);
                    Assert.Equal("1 ROI(s)", o.Detail);
                    Assert.Equal("linked to no image series in this study", o.Link);
                    Assert.True(o.IsUnlinked);
                },
                p =>
                {
                    // The scanner links no plan, so a plan is listed, never flagged.
                    Assert.Equal("RTPLAN", p.Kind);
                    Assert.Equal("1 file(s)", p.Detail);
                    Assert.StartsWith("listed only", p.Link);
                    Assert.False(p.IsUnlinked);
                });

            // The patient level counts the same object, and its study row carries the badge.
            var patient = new PatientInspectorViewModel(new DicomPatientGroup
            {
                PatientID = "SYN002",
                Studies = new List<DicomStudyGroup> { model },
            });
            Assert.Equal(1, patient.UnlinkedObjectCount);
            Assert.Equal("1 linked, 1 unlinked", patient.RtStructText);
            Assert.Equal("none", patient.RtDoseText);
            Assert.True(Assert.Single(patient.Studies).HasUnlinkedObjects);
            Assert.Equal(StudyInspectorViewModel.UnlinkedNote, patient.UnlinkedObjectsNote);
        }

        [Fact]
        public void A_study_with_only_rt_objects_shows_them_all_unlinked()
        {
            // What the scanner produces when a structure set's images are in another study.
            var model = Study("", "", RtObject("RTSTRUCT", "Contours", "PTV"), RtObject("RTDOSE", "Dose"));

            var vm = new StudyInspectorViewModel(model);

            Assert.Equal("(no study description)", vm.Title);
            Assert.Equal(InspectorViewModel.Missing, vm.StudyDateText);
            Assert.False(vm.HasImageSeries);
            Assert.Equal("none", vm.ImageSeriesSummary);
            Assert.Equal(InspectorViewModel.Missing, vm.FramesText);
            Assert.Equal(2, vm.UnlinkedObjectCount);
            Assert.Equal("2 unlinked", vm.UnlinkedBadgeText);
            Assert.All(vm.RtObjects, o => Assert.True(o.IsUnlinked));

            var patient = new PatientInspectorViewModel(new DicomPatientGroup
            {
                PatientID = "SYN003",
                Studies = new List<DicomStudyGroup> { model },
            });
            Assert.Equal("SYN003", patient.Title);
            Assert.Equal(InspectorViewModel.Missing, patient.PatientName);
            Assert.Equal("none", patient.ImageSeriesSummary);
            Assert.Equal("1 unlinked", patient.RtStructText);
            Assert.Equal("1 unlinked", patient.RtDoseText);
            Assert.Equal("2 unlinked", patient.UnlinkedBadgeText);
        }

        [Fact]
        public void A_study_whose_series_span_two_frames_of_reference_numbers_them_and_says_why_it_matters()
        {
            var ct = Image("CT", "Planning CT", FrameA, 0, 2.5, 5);
            var cbct = Image("CT", "CBCT", FrameA, 0, 2.5);
            var mr = Image("MR", "MR sim", FrameB, 0, 1, 2);
            var dose = RtObject("RTDOSE", "Plan dose");
            LinkDose(ct, dose, RtLinkMatchRule.FrameOfReferenceUid);
            LinkDose(cbct, dose, RtLinkMatchRule.FrameOfReferenceUid);

            var vm = new StudyInspectorViewModel(Study("20240305", "Planning", ct, cbct, mr, dose));

            Assert.Equal(2, vm.FrameOfReferenceCount);
            Assert.True(vm.HasMultipleFrames);
            Assert.Equal("2", vm.FramesText);
            Assert.Contains("span 2 frames of reference", vm.MultipleFramesNote);
            Assert.Equal(new[] { "frame 1", "frame 1", "frame 2" }, vm.ImageSeries.Select(s => s.FrameLabel));
            Assert.Equal("3 instance(s) · frame 1", vm.ImageSeries[0].Detail);
            Assert.Equal("2 CT, 1 MR", vm.ImageSeriesSummary);

            // A dose is linked to every series of its frame; the row names them all.
            Assert.Equal("linked to [CT] Planning CT, [CT] CBCT, by frame of reference UID", Assert.Single(vm.RtObjects).Link);

            // One frame: no numbering and no note.
            var single = new StudyInspectorViewModel(Study("20240305", "Planning", Image("CT", "Planning CT", FrameA, 0, 2.5)));
            Assert.Equal(1, single.FrameOfReferenceCount);
            Assert.False(single.HasMultipleFrames);
            Assert.Equal("1", single.FramesText);
            Assert.Equal("", single.MultipleFramesNote);
            Assert.Equal("", Assert.Single(single.ImageSeries).FrameLabel);

            // A series without a frame is counted out of the frames and labelled as such.
            var mixed = new StudyInspectorViewModel(Study("20240305", "Planning",
                Image("CT", "Planning CT", FrameA, 0, 2.5), Image("MR", "MR sim", FrameB, 0, 1), Image("PT", "PET", "", 0, 3)));
            Assert.Equal("2 (1 image series carry none)", mixed.FramesText);
            Assert.Equal("no frame of reference", mixed.ImageSeries[2].FrameLabel);
        }

        [Fact]
        public void The_tag_tabs_read_the_patient_and_study_rows_of_the_curated_image_list_in_its_order()
        {
            var curated = MetadataTagCatalog.CuratedImageKeywords.ToList();
            foreach (var keywords in new[] { PatientInspectorViewModel.PatientKeywords, StudyInspectorViewModel.StudyKeywords })
            {
                Assert.All(keywords, k => Assert.Contains(k, curated));
                Assert.Equal(keywords.OrderBy(k => curated.IndexOf(k)), keywords);
            }
            Assert.Equal(curated.Where(k => k.StartsWith("Study")), StudyInspectorViewModel.StudyKeywords);
        }

        [Theory]
        [InlineData("20240305", "2024-03-05")]
        [InlineData(" 20240305 ", "2024-03-05")]
        [InlineData("2024", "2024")]
        [InlineData("", InspectorViewModel.Missing)]
        [InlineData(null, InspectorViewModel.Missing)]
        public void Dicom_dates_read_as_iso_dates_and_anything_else_as_written(string value, string expected)
        {
            Assert.Equal(expected, InspectorViewModel.FormatDicomDate(value));
        }
    }
}
