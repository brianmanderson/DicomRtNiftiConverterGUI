using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The <see cref="AnalysisCohort"/>, scanned once and analysed with and without its associations.</summary>
    public sealed class ScannedAnalysisCohort : IDisposable
    {
        public string Root { get; } = DicomTestData.NewTempDir();
        public DicomScanResult Scan { get; }
        public CohortAnalysis Analysis { get; }
        public CohortAnalysis WithoutAssociations { get; }

        public ScannedAnalysisCohort()
        {
            AnalysisCohort.Write(Root);
            Scan = new DicomScannerService().ScanFolderAsync(Root, null, CancellationToken.None).GetAwaiter().GetResult();
            Analysis = CohortAnalysisService.Analyze(Scan, new CohortAnalysisOptions
            {
                InputRoot = Root,
                Associations = AnalysisCohort.Associations(),
                AssociationsName = "associations.json",
            });
            WithoutAssociations = CohortAnalysisService.Analyze(Scan, new CohortAnalysisOptions { InputRoot = Root });
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// One test per flag code: where the analysis cohort raises it, that it is raised nowhere else
    /// there, and what must not raise it. The codes that mirror a desktop-app badge are also held
    /// against a verbatim copy of the view-model rule they replace (cited at toolkit a40fd83), so
    /// the GUI and the analysis cannot drift apart before the GUI adopts <see cref="SeriesFlags"/>.
    /// </summary>
    public class CohortAnalysisFlagTests : IClassFixture<ScannedAnalysisCohort>
    {
        private readonly ScannedAnalysisCohort _cohort;

        public CohortAnalysisFlagTests(ScannedAnalysisCohort cohort)
        {
            _cohort = cohort;
        }

        // ---- The catalogue ------------------------------------------------------------------

        [Fact]
        public void TheCatalogue_ListsEveryCodeOnce_WithAKnownSeverity()
        {
            var constants = typeof(FlagCodes).GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(constants, FlagCodes.All.Select(d => d.Code).OrderBy(c => c, StringComparer.Ordinal));
            Assert.All(FlagCodes.All, d =>
            {
                Assert.Contains(d.Severity, new[] { FlagSeverity.Info, FlagSeverity.Warning, FlagSeverity.Serious });
                Assert.False(string.IsNullOrWhiteSpace(d.Meaning));
            });
        }

        [Fact]
        public void EverySeriesFlagList_IsTheCodesRaisedOnIt_InCatalogueOrder()
        {
            foreach (var series in _cohort.Analysis.Series)
            {
                var raised = _cohort.Analysis.Flags
                    .Where(f => f.SeriesUid == series.SeriesUid)
                    .Select(f => f.Code)
                    .Distinct()
                    .OrderBy(FlagCodes.OrderOf)
                    .ToList();
                Assert.Equal(raised, series.Flags);
            }
        }

        // ---- GEOM_* -------------------------------------------------------------------------

        [Fact]
        public void GEOM_NONUNIFORM_Z_IsTheConversionWarning_OnTheMixedGapSeriesOnly()
        {
            var flag = Assert.Single(Raised(FlagCodes.GeomNonUniformZ));
            Assert.Equal("2.2.1.1", flag.SeriesUid);
            Assert.True(SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(Image("2.2.1.1"), out string warning));
            Assert.Equal(warning, flag.Message);
            Assert.Equal(FlagSeverity.Serious, flag.Severity);

            // Gaps that differ only by rounding are non-uniform by the absolute test but raise
            // nothing, exactly as the badge does not.
            var rounding = Analyze(Study(Ct("9.1", z: new[] { 0.0, 0.62, 1.25, 1.87, 2.5 })));
            Assert.DoesNotContain(rounding.Flags, f => f.Code == FlagCodes.GeomNonUniformZ);
            Assert.Equal(1, rounding.Geometry.NonUniformWithinTolerance);
        }

        [Fact]
        public void GEOM_UNPOSITIONED_IsTheConversionWarning_OnTheSeriesWithASkippedSlice()
        {
            var flag = Assert.Single(Raised(FlagCodes.GeomUnpositioned));
            Assert.Equal("2.2.1.1", flag.SeriesUid);
            Assert.True(SeriesGeometryProbe.TryBuildMissingPositionWarning(Image("2.2.1.1"), out string warning));
            Assert.Equal(warning, flag.Message);
            Assert.Contains("ct_x.dcm", flag.Message);
            Assert.Equal(1, _cohort.Analysis.Series.Single(s => s.SeriesUid == "2.2.1.1").Geometry.UnpositionedSlices);
        }

        [Fact]
        public void GEOM_OBLIQUE_IsRaisedForATiltedNormal_NotForAnInPlaneRotation()
        {
            var flag = Assert.Single(Raised(FlagCodes.GeomOblique));
            Assert.Equal("2.1.1.2", flag.SeriesUid);
            var mr = _cohort.Analysis.Series.Single(s => s.SeriesUid == "2.1.1.2");
            Assert.Equal(SeriesOrientation.Oblique, mr.Orientation.Plane);
            Assert.Equal(10.0, mr.Orientation.NormalTiltDegrees, 3);

            var rotated = Ct("9.2", orientation: AnalysisCohort.InPlaneRotated30);
            var coronal = Ct("9.3", orientation: new[] { 1.0, 0, 0, 0, 0, -1.0 });
            var analysis = Analyze(Study(rotated), Study(coronal));

            var rotatedSeries = analysis.Series.Single(s => s.SeriesUid == "9.2");
            Assert.Equal(SeriesOrientation.Axial, rotatedSeries.Orientation.Plane);
            Assert.False(rotatedSeries.Orientation.AxisAligned);
            Assert.DoesNotContain(FlagCodes.GeomOblique, rotatedSeries.Flags);

            var coronalSeries = analysis.Series.Single(s => s.SeriesUid == "9.3");
            Assert.Equal(SeriesOrientation.Coronal, coronalSeries.Orientation.Plane);
            Assert.Contains(FlagCodes.GeomOblique, coronalSeries.Flags);
        }

        [Fact]
        public void GEOM_MIXED_ORIENTATION_IsRaisedWhenTheFilesDisagree_AndNoOrientationIsReported()
        {
            var flag = Assert.Single(Raised(FlagCodes.GeomMixedOrientation));
            Assert.Equal("2.3.1.1", flag.SeriesUid);
            var series = _cohort.Analysis.Series.Single(s => s.SeriesUid == "2.3.1.1");
            Assert.Null(series.Orientation);
            Assert.True(Image("2.3.1.1").OrientationVaries);
            Assert.DoesNotContain(FlagCodes.GeomOblique, series.Flags);
        }

        [Fact]
        public void GEOM_MODALITY_MINORITY_NamesTheOutvotedInstances()
        {
            var flag = Assert.Single(Raised(FlagCodes.GeomModalityMinority));
            Assert.Equal("2.2.1.1", flag.SeriesUid);
            Assert.Equal("series 2.2.1.1: 1 of 7 instance(s) carry another Modality (OT 1); the series is CT by majority.", flag.Message);
            Assert.Equal(FlagSeverity.Info, flag.Severity);
        }

        // ---- LINK_* -------------------------------------------------------------------------

        [Fact]
        public void LINK_FALLBACK_IsRaisedPerGuessedObject_AndMatchesTheLinkGuessedBadge()
        {
            var flag = Assert.Single(Raised(FlagCodes.LinkFallback));
            Assert.Equal("2.2.1.1", flag.SeriesUid);
            Assert.Equal("2.2.1.3", flag.ObjectUid);

            foreach (var (image, series) in ImagesWithAnalysis(_cohort.Scan, _cohort.Analysis))
                Assert.Equal(GuiAnyLinkGuessed(image), series.Flags.Contains(FlagCodes.LinkFallback));
            foreach (var image in AllImages(_cohort.Scan))
                Assert.Equal(GuiAnyLinkGuessed(image), SeriesFlags.AnyLinkGuessed(image));
        }

        [Fact]
        public void LINK_UNLINKED_IsRaisedForTheOrphanDose_AndMatchesTheUnlinkedBadge()
        {
            var flag = Assert.Single(Raised(FlagCodes.LinkUnlinked));
            Assert.Null(flag.SeriesUid);
            Assert.Equal("2.3.2.5", flag.ObjectUid);
            Assert.Equal("2.3.2", flag.StudyUid);

            var unlinked = _cohort.Analysis.Linkage.Unlinked.Select(u => u.ObjectUid).ToList();
            foreach (var study in _cohort.Scan.Patients.SelectMany(p => p.Studies))
            {
                foreach (var obj in study.Series.Where(s => !GuiIsImageSeries(s)))
                {
                    Assert.Equal(GuiIsUnlinked(study, obj), SeriesFlags.IsUnlinked(study, obj));
                    Assert.Equal(GuiIsUnlinked(study, obj), unlinked.Contains(obj.SeriesInstanceUID));
                }
            }
        }

        [Fact]
        public void LINK_FOR_MISMATCH_IsRaisedForAReferencedSeriesInAnotherFrame_NotForOneThatNamesItsFrameToo()
        {
            var flag = Assert.Single(Raised(FlagCodes.LinkForMismatch));
            Assert.Equal("2.1.1.1", flag.SeriesUid);
            Assert.Equal("2.1.1.4", flag.ObjectUid);
            Assert.Equal(FlagSeverity.Serious, flag.Severity);

            // A structure set drawn across two frames (a planning CT and a registered MR) that
            // names the image's frame among them is not a mismatch.
            var image = Ct("9.4", frame: "9.4.90");
            var rs = new DicomSeriesGroup
            {
                SeriesInstanceUID = "9.4.3", Modality = "RTSTRUCT", FrameOfReferenceUID = "9.4.91",
                FrameOfReferenceUIDs = { "9.4.91", "9.4.90" }, ReferencedSeriesUID = "9.4",
                LinkMatchRule = RtLinkMatchRule.ReferencedSeriesUid,
            };
            Link(image, rs);
            Assert.False(SeriesFlags.HasFrameOfReferenceMismatch(image, rs));
            rs.FrameOfReferenceUIDs = new List<string> { "9.4.91" };
            Assert.True(SeriesFlags.HasFrameOfReferenceMismatch(image, rs));
            rs.LinkMatchRule = RtLinkMatchRule.FrameOfReferenceUid;
            Assert.False(SeriesFlags.HasFrameOfReferenceMismatch(image, rs));
        }

        [Fact]
        public void LINK_REFERENCED_SERIES_ABSENT_IsRaisedForAStructureSetWhoseSeriesIsMissing()
        {
            var flag = Assert.Single(Raised(FlagCodes.LinkReferencedSeriesAbsent));
            Assert.Equal("2.2.1.1", flag.SeriesUid);
            Assert.Equal("2.2.1.3", flag.ObjectUid);
            Assert.Contains("references image series 2.2.1.8", flag.Message);
            var entry = Assert.Single(_cohort.Analysis.Linkage.ReferencedSeriesAbsent);
            Assert.Equal("2.2.1.8", entry.ReferencedSeries);
            Assert.Equal(new[] { "2.2.1.1" }, entry.LinkedTo);

            // A dose's referenced UID is a structure set's instance, never judged.
            var study = Study(Ct("9.5"));
            var dose = new DicomSeriesGroup { SeriesInstanceUID = "9.5.5", Modality = "RTDOSE", ReferencedSeriesUID = "1.2.3" };
            study.Series.Add(dose);
            Assert.False(SeriesFlags.ReferencesAbsentSeries(study, dose));
        }

        [Fact]
        public void LINK_MULTIPLE_FRAMES_IsRaisedOncePerStudy_AndMatchesTheStudyInspector()
        {
            var flag = Assert.Single(Raised(FlagCodes.LinkMultipleFrames));
            Assert.Equal("2.1.1", flag.StudyUid);
            Assert.Null(flag.SeriesUid);

            foreach (var study in _cohort.Scan.Patients.SelectMany(p => p.Studies))
            {
                Assert.Equal(GuiHasMultipleFrames(study, out int frames), SeriesFlags.HasMultipleFrames(study, out int ours));
                Assert.Equal(frames, ours);
            }
        }

        // ---- RT_* and ROI_* -----------------------------------------------------------------

        [Fact]
        public void RT_MULTIPLE_STRUCTS_NamesTheStructureSetAnExportTakes()
        {
            var flag = Assert.Single(Raised(FlagCodes.RtMultipleStructs));
            Assert.Equal("2.1.1.1", flag.SeriesUid);
            Assert.Contains("the first linked (2.1.1.3)", flag.Message);
            var entry = Assert.Single(_cohort.Analysis.Linkage.MultiStructSeries);
            Assert.Equal(new[] { "2.1.1.3", "2.1.1.4" }, entry.StructureSets);
        }

        [Fact]
        public void ROI_DUPLICATE_NAME_IsRaisedForANameListedTwiceInOneStructureSet()
        {
            var flag = Assert.Single(Raised(FlagCodes.RoiDuplicateName));
            Assert.Equal("2.1.1.3", flag.ObjectUid);
            Assert.Equal("GTV", flag.Roi);
            Assert.Equal(new[] { "GTV", "Spinal-Cord", "Lung_L" }, Rt("2.1.1.3").RoiNames);
            Assert.Equal(new[] { "GTV" }, Rt("2.1.1.3").DuplicateRoiNames);
            Assert.Single(_cohort.Analysis.Series.SelectMany(s => s.Rois), r => r.Duplicate);
        }

        [Fact]
        public void ROI_UNASSOCIATED_IsRaisedPerUncoveredName_OnlyWithAnAssociationSet()
        {
            Assert.Equal(new[] { "GTV", "Heart", "PTV 70" }, Raised(FlagCodes.RoiUnassociated).Select(f => f.Roi));
            Assert.DoesNotContain(_cohort.WithoutAssociations.Flags, f => f.Code == FlagCodes.RoiUnassociated);

            var associations = AnalysisCohort.Associations();
            foreach (var name in _cohort.Analysis.RoiNaming.Names.Select(n => n.Name))
                Assert.Equal(!GuiIsNameCovered(name, associations), SeriesFlags.IsUnassociated(name, associations));
        }

        // ---- Verbatim copies of the view-model rules, at toolkit a40fd83 ----------------------

        /// <summary>PatientGroupViewModel.cs:109-110 (StudyGroupViewModel.IsImageSeries).</summary>
        private static bool GuiIsImageSeries(DicomSeriesGroup series)
            => series != null && (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT");

        /// <summary>PatientGroupViewModel.cs:195-199 (SeriesGroupViewModel.AnyLinkGuessed).</summary>
        private static bool GuiAnyLinkGuessed(DicomSeriesGroup series)
            => new[] { series.LinkedRtStruct, series.LinkedRtDose }
                .Concat(series.LinkedRtStructs ?? Enumerable.Empty<DicomSeriesGroup>())
                .Concat(series.LinkedRtDoses ?? Enumerable.Empty<DicomSeriesGroup>())
                .Any(rt => rt != null && rt.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);

        /// <summary>StudyInspectorViewModel.cs:185-205 and 229-233 (StudyContents.IsUnlinked, LinksTo).</summary>
        private static bool GuiIsUnlinked(DicomStudyGroup study, DicomSeriesGroup rtObject)
        {
            var imageSeries = study.Series.Where(GuiIsImageSeries).ToList();
            bool isLinkable = rtObject.Modality == "RTSTRUCT" || rtObject.Modality == "RTDOSE";
            return isLinkable && !imageSeries.Any(image =>
                ReferenceEquals(image.LinkedRtStruct, rtObject)
                || ReferenceEquals(image.LinkedRtDose, rtObject)
                || (image.LinkedRtStructs?.Any(s => ReferenceEquals(s, rtObject)) ?? false)
                || (image.LinkedRtDoses?.Any(d => ReferenceEquals(d, rtObject)) ?? false));
        }

        /// <summary>StudyInspectorViewModel.cs:51-58 (FrameOfReferenceCount, HasMultipleFrames).</summary>
        private static bool GuiHasMultipleFrames(DicomStudyGroup study, out int frameCount)
        {
            var frames = study.Series.Where(GuiIsImageSeries)
                .Select(s => s.FrameOfReferenceUID)
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            frameCount = frames.Count;
            return frames.Count > 1;
        }

        /// <summary>RoiAssociationViewModel.cs:206-217 (IsNameCovered).</summary>
        private static bool GuiIsNameCovered(string name, List<RoiAssociation> associations)
        {
            foreach (var assoc in associations)
            {
                if (RoiNameMatcher.Matches(name, assoc.CanonicalName))
                    return true;
                foreach (var alias in assoc.Aliases)
                    if (RoiNameMatcher.Matches(name, alias))
                        return true;
            }
            return false;
        }

        // ---- Helpers --------------------------------------------------------------------------

        private List<AnalysisFlag> Raised(string code) => _cohort.Analysis.Flags.Where(f => f.Code == code).ToList();

        private DicomSeriesGroup Image(string uid) => AllImages(_cohort.Scan).Single(s => s.SeriesInstanceUID == uid);

        private DicomSeriesGroup Rt(string uid) =>
            _cohort.Scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series).Single(s => s.SeriesInstanceUID == uid);

        private static IEnumerable<DicomSeriesGroup> AllImages(DicomScanResult scan) =>
            scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series).Where(GuiIsImageSeries);

        private static IEnumerable<(DicomSeriesGroup, SeriesAnalysis)> ImagesWithAnalysis(DicomScanResult scan, CohortAnalysis analysis) =>
            AllImages(scan).Select(image => (image, analysis.Series.Single(s => s.SeriesUid == image.SeriesInstanceUID)));

        /// <summary>An in-memory CT: axial, 0.7 \ 0.9 mm, at <paramref name="z"/> (default 0, 2, 4).</summary>
        internal static DicomSeriesGroup Ct(string uid, double[] z = null, double[] orientation = null, string frame = null)
        {
            z = z ?? new[] { 0.0, 2.0, 4.0 };
            return new DicomSeriesGroup
            {
                SeriesInstanceUID = uid,
                Modality = "CT",
                FrameOfReferenceUID = frame ?? uid + ".90",
                FilePaths = z.Select((_, i) => $"ct_{i}.dcm").ToList(),
                SlicePositions = z.ToList(),
                PixelSpacing = new[] { 0.7, 0.9 },
                ImageOrientationPatient = orientation ?? AnalysisCohort.Axial,
                Rows = 16,
                Columns = 20,
                ModalityCounts = { { "CT", z.Length } },
            };
        }

        internal static DicomStudyGroup Study(params DicomSeriesGroup[] series)
        {
            var study = new DicomStudyGroup { StudyInstanceUID = series.FirstOrDefault()?.SeriesInstanceUID + ".s" };
            study.Series.AddRange(series);
            return study;
        }

        internal static void Link(DicomSeriesGroup image, DicomSeriesGroup rtStruct)
        {
            image.LinkedRtStructs.Add(rtStruct);
            image.LinkedRtStruct = image.LinkedRtStruct ?? rtStruct;
        }

        internal static CohortAnalysis Analyze(params DicomStudyGroup[] studies)
        {
            var patient = new DicomPatientGroup { PatientID = "P" };
            patient.Studies.AddRange(studies);
            return CohortAnalysisService.Analyze(new DicomScanResult { Patients = { patient } }, new CohortAnalysisOptions());
        }
    }
}
