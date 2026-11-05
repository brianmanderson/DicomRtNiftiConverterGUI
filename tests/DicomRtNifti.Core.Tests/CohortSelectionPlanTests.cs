using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The two selections a front-end with a tree to tick needs and the description filters cannot
    /// express (W4-C): which image series, by SeriesInstanceUID, and which of a series' several
    /// structure sets, by theirs. Both are planning rules, so these run on an in-memory scan
    /// without the SimpleITK native.
    ///
    /// The model: PAT_1 has a planning CT carrying two structure sets (planning, then review, in
    /// linked order) and a CBCT carrying one; PAT_2 has one CT and one structure set.
    /// </summary>
    public class CohortSelectionPlanTests
    {
        private const string Planning1 = "1.2.826.1.1", Cbct1 = "1.2.826.1.2", Ct2 = "1.2.826.2.1";
        private const string RsPlan = "1.2.826.1.11", RsReview = "1.2.826.1.12", RsCbct = "1.2.826.1.21",
            Rs2 = "1.2.826.2.11";

        private readonly DicomScanResult _scan = BuildScan();

        // ---- Series selection ------------------------------------------------------------------

        [Fact]
        public void SeriesSelection_KeepsTheListedSeries_AndReportsEveryOtherInSkipped()
        {
            var plan = Plan(new CohortExportOptions { SeriesInstanceUids = { Planning1, Ct2 } });

            Assert.Equal(new[] { Planning1, Ct2 }, plan.Series.Select(s => s.SeriesUid));
            var skipped = Assert.Single(plan.Skipped);
            Assert.Equal(Cbct1, skipped.SeriesInstanceUid);
            Assert.Equal("PAT_1", skipped.PatientId);
            Assert.Equal("CBCT", skipped.SeriesDescription);
            Assert.Equal("SeriesInstanceUID is not in the series selection", skipped.Reason);
        }

        [Fact]
        public void SeriesSelection_RunsAfterThePatientFilter()
        {
            // PAT_2 is filtered out by patient, silently, as it always was: the series selection
            // reports only what it removed from the patients that were kept.
            var plan = Plan(new CohortExportOptions { PatientIds = { "PAT_1" }, SeriesInstanceUids = { Planning1, Ct2 } });

            Assert.Equal(Planning1, Assert.Single(plan.Series).SeriesUid);
            Assert.Equal(Cbct1, Assert.Single(plan.Skipped).SeriesInstanceUid);
        }

        [Fact]
        public void SeriesSelection_RunsBeforeTheDescriptionFilters()
        {
            // The CBCT matches the description filter but is not selected: it is reported for the
            // selection. The planning CT is selected but fails the filter: reported for the filter.
            var plan = Plan(new CohortExportOptions
            {
                SeriesInstanceUids = { Planning1 },
                SeriesDescriptionFilter = "CBCT",
            });

            Assert.Empty(plan.Series);
            var reasons = plan.Skipped.ToDictionary(s => s.SeriesInstanceUid, s => s.Reason);
            Assert.Equal(3, reasons.Count);
            Assert.Equal("SeriesInstanceUID is not in the series selection", reasons[Cbct1]);
            Assert.Equal("SeriesInstanceUID is not in the series selection", reasons[Ct2]);
            Assert.Equal("SeriesDescription does not contain 'CBCT'", reasons[Planning1]);
        }

        [Fact]
        public void SeriesSelection_MatchesWholeUidsOnly()
        {
            // A prefix of a UID, or one that differs only in a trailing component, is not that series.
            var plan = Plan(new CohortExportOptions { SeriesInstanceUids = { "1.2.826.1", Planning1 + ".0" } });

            Assert.Empty(plan.Series);
            Assert.Equal(3, plan.Skipped.Count);
        }

        [Fact]
        public void SeriesSelection_ReportsHashedIdentifiers_WhenAnonymizing()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                var anon = new AnonymizationService(Path.Combine(dir, "key.json"), "salt");
                var plan = CohortExportService.BuildPlan(_scan,
                    new CohortExportOptions { Anonymize = true, SeriesInstanceUids = { Planning1 } }, anon);

                Assert.Equal(2, plan.Skipped.Count);
                Assert.All(plan.Skipped, s =>
                {
                    Assert.DoesNotContain("PAT_", s.PatientId);
                    Assert.DoesNotContain("1.2.826", s.SeriesInstanceUid);
                });
                Assert.Contains(plan.Skipped, s => s.SeriesInstanceUid == anon.GetSeriesHash(Cbct1));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void NoSeriesSelection_PlansEveryImageSeries()
        {
            var plan = Plan(new CohortExportOptions());

            Assert.Equal(new[] { Planning1, Cbct1, Ct2 }, plan.Series.Select(s => s.SeriesUid));
            Assert.Empty(plan.Skipped);
        }

        // ---- Structure-set choice ---------------------------------------------------------------

        [Fact]
        public void StructureSetChoice_DefaultsToTheFirstLinked()
        {
            var plan = Plan(new CohortExportOptions());

            Assert.Equal(new[] { RsPlan, RsCbct, Rs2 }, plan.Series.Select(s => s.RtStruct.SeriesInstanceUID));
        }

        [Fact]
        public void StructureSetChoice_TheListedOneWins_AndIsNotAFilter()
        {
            // Only the planning CT's second set is listed; the other two series keep their first
            // set, and nothing is skipped.
            var plan = Plan(new CohortExportOptions { StructureSetUids = { RsReview } });

            Assert.Equal(new[] { RsReview, RsCbct, Rs2 }, plan.Series.Select(s => s.RtStruct.SeriesInstanceUID));
            Assert.Empty(plan.Skipped);
        }

        [Fact]
        public void StructureSetChoice_TheListedOneBeatsTheDescriptionFilter_WhichBeatsTheFirst()
        {
            // "review" alone picks the review set over the first-linked planning set...
            var byDescription = Plan(new CohortExportOptions { StructDescriptionFilter = "review" });
            Assert.Equal(RsReview, Assert.Single(byDescription.Series).RtStruct.SeriesInstanceUID);

            // ...and a listed planning set beats it. The filter still decides which series are kept.
            var listed = Plan(new CohortExportOptions { StructDescriptionFilter = "review", StructureSetUids = { RsPlan } });
            var series = Assert.Single(listed.Series);
            Assert.Equal(Planning1, series.SeriesUid);
            Assert.Equal(RsPlan, series.RtStruct.SeriesInstanceUID);
            Assert.Equal(new[] { Cbct1, Ct2 }, listed.Skipped.Select(s => s.SeriesInstanceUid));
        }

        [Fact]
        public void StructureSetChoice_TwoListedOnOneSeries_TakesTheFirstInLinkedOrder()
        {
            var plan = Plan(new CohortExportOptions { StructureSetUids = { RsReview, RsPlan } });

            Assert.Equal(RsPlan, plan.Series.First(s => s.SeriesUid == Planning1).RtStruct.SeriesInstanceUID);
        }

        [Fact]
        public void StructureSetChoice_OneListNamesASetForEachOfSeveralSeries()
        {
            var plan = Plan(new CohortExportOptions { StructureSetUids = { RsReview, Rs2, "1.2.826.9.9" } });

            Assert.Equal(new[] { RsReview, RsCbct, Rs2 }, plan.Series.Select(s => s.RtStruct.SeriesInstanceUID));
        }

        [Fact]
        public void SeriesSelectionAndStructureSetChoice_Together_AreWhatAFrontEndSends()
        {
            var plan = Plan(new CohortExportOptions
            {
                SeriesInstanceUids = { Planning1 },
                StructureSetUids = { RsReview },
                RequireStructures = true,
            });

            var series = Assert.Single(plan.Series);
            Assert.Equal(RsReview, series.RtStruct.SeriesInstanceUID);
            Assert.Equal(new[] { Cbct1, Ct2 }, plan.Skipped.Select(s => s.SeriesInstanceUid));
        }

        private CohortExportPlan Plan(CohortExportOptions options) =>
            CohortExportService.BuildPlan(_scan, options, null);

        private static DicomScanResult BuildScan()
        {
            DicomSeriesGroup Image(string uid, string description, int slices) => new DicomSeriesGroup
            {
                SeriesInstanceUID = uid,
                SeriesDescription = description,
                Modality = "CT",
                FilePaths = Enumerable.Range(0, slices).Select(i => uid + "_" + i + ".dcm").ToList(),
            };
            DicomSeriesGroup Structures(string uid, string description, params string[] rois) => new DicomSeriesGroup
            {
                SeriesInstanceUID = uid,
                SeriesDescription = description,
                Modality = "RTSTRUCT",
                LinkMatchRule = RtLinkMatchRule.ReferencedSeriesUid,
                FilePaths = { uid + ".dcm" },
                RoiNames = rois.ToList(),
            };

            var planning = Image(Planning1, "Planning CT", 20);
            var cbct = Image(Cbct1, "CBCT", 8);
            var ct2 = Image(Ct2, "CT", 10);
            var rsPlan = Structures(RsPlan, "planning ROIs", "Heart", "Lung_L");
            var rsReview = Structures(RsReview, "review ROIs", "Heart");
            var rsCbct = Structures(RsCbct, "cbct ROIs", "Heart");
            var rs2 = Structures(Rs2, "planning ROIs 2", "Liver");
            planning.LinkedRtStructs.AddRange(new[] { rsPlan, rsReview });
            cbct.LinkedRtStructs.Add(rsCbct);
            ct2.LinkedRtStructs.Add(rs2);

            return new DicomScanResult
            {
                Patients =
                {
                    new DicomPatientGroup
                    {
                        PatientID = "PAT_1",
                        Studies =
                        {
                            new DicomStudyGroup
                            {
                                StudyInstanceUID = "1.2.826.1",
                                Series = { planning, cbct, rsPlan, rsReview, rsCbct },
                            },
                        },
                    },
                    new DicomPatientGroup
                    {
                        PatientID = "PAT_2",
                        Studies =
                        {
                            new DicomStudyGroup { StudyInstanceUID = "1.2.826.2", Series = { ct2, rs2 } },
                        },
                    },
                },
            };
        }
    }
}
