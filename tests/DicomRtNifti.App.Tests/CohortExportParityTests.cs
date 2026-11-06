using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Core.Tests;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// Does the GUI's Convert Selected write what <c>CohortExportService.BuildPlan</c> +
    /// <c>ExecuteAsync</c> write for the same export stated as the CLI's cohort options? W3-D.1
    /// pinned where the window's own export loop agreed with the service and named where it did
    /// not (#50: colliding folders overwritten; #91: only the first dose; #92: manifest ROI columns
    /// in hash order). W3-D.2 moved the window onto the service, resolving all three in the
    /// service's favour, so every aspect of every case is asserted equal and none is skipped. What
    /// is left to catch is a slip in the window's mapping of its controls to options
    /// (MainViewModel.ExportOptionsFor), which cases 5 to 7 exercise: the ROI selection, a
    /// structure set picked on the series row, and the Require switches with a description filter.
    ///
    /// Each case synthesizes a cohort, exports it once through each path
    /// (<see cref="ExportParity"/>), and compares, byte for byte: every file and folder under the
    /// output root with its SHA-256, the manifest CSV, and the anonymization key; and the manifest
    /// once more read by column name. Each aspect is its own test, so a case that disagrees on one
    /// still pins the others. A disagreement is a finding to fix in the window, never an expected
    /// result written down here.
    ///
    /// Lives in App.Tests because only this project reaches both paths: it references the App
    /// (MainViewModel, driven headless through the real launcher) and, through it, Core
    /// (CohortExportService), and it already compiles in Core.Tests' DicomTestData. Core.Tests
    /// cannot reference the App without pulling Avalonia into Core's test lane.
    /// </summary>
    public class CohortExportParityTests
    {
        private readonly ITestOutputHelper _out;

        public CohortExportParityTests(ITestOutputHelper output) => _out = output;

        // ── case 1: the default options ───────────────────────────────────────────────────

        /// <summary>
        /// The window's defaults (images, structures and dose; native spacing; no associations;
        /// not anonymized) over two patients, each a CT with a structure set and one dose. The
        /// patients carry different ROI sets, so the manifest's ROI columns are a union across
        /// series and their order is tested too.
        /// </summary>
        private static Task<ParityRun> DefaultRun() => ExportParity.RunOnceAsync(
            "default", Cohorts.TwoPatientsWithStructuresAndDose, new ExportToggles());

        [AvaloniaFact]
        public async Task Default_options_write_the_same_files()
            => AssertSameTree(await DefaultRun());

        [AvaloniaFact]
        public async Task Default_options_write_the_same_manifest()
            => AssertSameManifest(await DefaultRun());

        [AvaloniaFact]
        public async Task Default_options_write_the_same_manifest_cells()
            => AssertSameManifestCells(await DefaultRun());

        // ── case 2: anonymize + output spacing + associations ─────────────────────────────

        /// <summary>
        /// The same cohort anonymized under a non-default salt, resampled to 2 × 2 × 1.5 mm, with
        /// two associations that rename an ROI each while the unmatched ROIs keep their raw names.
        /// </summary>
        private static Task<ParityRun> AnonymizedRun() => ExportParity.RunOnceAsync(
            "anonymized-spacing-associations", Cohorts.TwoPatientsWithStructuresAndDose, new ExportToggles
            {
                Anonymize = true,
                Salt = "w3d1-parity-salt",
                OutputSpacing = new[] { 2.0, 2.0, 1.5 },
                Associations = new List<RoiAssociation>
                {
                    new RoiAssociation { CanonicalName = "GTV", Aliases = new List<string> { "Target" } },
                    new RoiAssociation { CanonicalName = "SpinalCord", Aliases = new List<string> { "Cord" } },
                },
            });

        [AvaloniaFact]
        public async Task Anonymized_resampled_associated_export_writes_the_same_files()
            => AssertSameTree(await AnonymizedRun());

        [AvaloniaFact]
        public async Task Anonymized_resampled_associated_export_writes_the_same_manifest()
            => AssertSameManifest(await AnonymizedRun());

        [AvaloniaFact]
        public async Task Anonymized_resampled_associated_export_writes_the_same_manifest_cells()
            => AssertSameManifestCells(await AnonymizedRun());

        [AvaloniaFact]
        public async Task Anonymized_resampled_associated_export_writes_the_same_key()
            => AssertSameBytes(await AnonymizedRun(), AnonymizationKeyLocation.FileName, s => s.Key);

        // ── case 3: two series of one patient with equal SeriesDate and SeriesDescription ──

        /// <summary>
        /// One patient whose study holds two CT series with the same SeriesDate and the same
        /// SeriesDescription (a planning CT and a re-export, say), of different lengths, each with
        /// its own structure set. Both plan the folder "&lt;date&gt;_CT series", so each is suffixed
        /// with a hash of its SeriesInstanceUID. Before W3-D.2 the GUI wrote the second into the
        /// first (F-c, #50).
        /// </summary>
        private static Task<ParityRun> CollidingRun() => ExportParity.RunOnceAsync(
            "colliding-folders", Cohorts.TwoSeriesWithEqualDateAndDescription, new ExportToggles());

        [AvaloniaFact]
        public async Task Two_series_with_equal_date_and_description_write_the_same_files()
            => AssertSameTree(await CollidingRun());

        [AvaloniaFact]
        public async Task Two_series_with_equal_date_and_description_write_the_same_manifest()
            => AssertSameManifest(await CollidingRun());

        [AvaloniaFact]
        public async Task Two_series_with_equal_date_and_description_write_the_same_manifest_cells()
            => AssertSameManifestCells(await CollidingRun());

        // ── case 4: one series with two structure sets and two doses ──────────────────────

        /// <summary>
        /// One CT carrying two structure sets (different ROIs) and two doses (different
        /// descriptions), all linked to it. With the series row's picker on Automatic, both paths
        /// export the first structure set, and both doses. Before W3-D.2 the GUI wrote only the
        /// first dose (#91).
        /// </summary>
        private static Task<ParityRun> TwoOfEachRun() => ExportParity.RunOnceAsync(
            "two-structure-sets-two-doses", Cohorts.OneSeriesWithTwoStructureSetsAndTwoDoses, new ExportToggles());

        [AvaloniaFact]
        public async Task Two_structure_sets_and_two_doses_write_the_same_files()
            => AssertSameTree(await TwoOfEachRun());

        [AvaloniaFact]
        public async Task Two_structure_sets_and_two_doses_write_the_same_manifest()
            => AssertSameManifest(await TwoOfEachRun());

        [AvaloniaFact]
        public async Task Two_structure_sets_and_two_doses_write_the_same_manifest_cells()
            => AssertSameManifestCells(await TwoOfEachRun());

        // ── case 5: Limit export to selected ROIs, with an association ────────────────────

        /// <summary>
        /// The case-1 cohort with one association (SpinalCord ← Cord) and "Limit export to
        /// selected ROIs" on, SpinalCord and Target ticked in Select ROIs: the service's
        /// <c>RoiSelection</c> (the CLI's --rois). The association keeps its alias, Target matches
        /// itself, and Avoid and Body are dropped. Both patients keep a selected ROI, so Select ROIs
        /// de-selects neither.
        /// </summary>
        private static Task<ParityRun> RoiSelectionRun() => ExportParity.RunOnceAsync(
            "roi-selection", Cohorts.TwoPatientsWithStructuresAndDose, new ExportToggles
            {
                Associations = new List<RoiAssociation>
                {
                    new RoiAssociation { CanonicalName = "SpinalCord", Aliases = new List<string> { "Cord" } },
                },
                SelectedRois = new List<string> { "SpinalCord", "Target" },
            });

        [AvaloniaFact]
        public async Task Selected_rois_write_the_same_files()
        {
            var run = await RoiSelectionRun();
            AssertSameTree(run);
            Assert.Equal(
                new[] { "PARITY01/masks/Target.nii.gz", "PARITY02/masks/SpinalCord.nii.gz", "PARITY02/masks/Target.nii.gz" },
                Masks(run.Gui));
        }

        [AvaloniaFact]
        public async Task Selected_rois_write_the_same_manifest()
            => AssertSameManifest(await RoiSelectionRun());

        // ── case 6: a structure set picked on the series row ──────────────────────────────

        /// <summary>
        /// The case-4 input with the second structure set (Body, Cord; 2.25.4130) picked on the
        /// series row: the service's <c>StructureSetUids</c> (the CLI's --struct-series, W4-C).
        /// The masks are Body and Cord, not case 4's Target and Avoid.
        /// </summary>
        private static Task<ParityRun> PickedStructureSetRun() => ExportParity.RunOnceAsync(
            "picked-structure-set", Cohorts.OneSeriesWithTwoStructureSetsAndTwoDoses, new ExportToggles
            {
                PickedStructureSet = "2.25.4130",
            });

        [AvaloniaFact]
        public async Task A_picked_structure_set_writes_the_same_files()
        {
            var run = await PickedStructureSetRun();
            AssertSameTree(run);
            Assert.Equal(new[] { "Body.nii.gz", "Cord.nii.gz" }, Masks(run.Gui).Select(m => m.Substring(m.LastIndexOf('/') + 1)));
        }

        [AvaloniaFact]
        public async Task A_picked_structure_set_writes_the_same_manifest()
            => AssertSameManifest(await PickedStructureSetRun());

        // ── case 7: Require a dose, and a series-description filter ───────────────────────

        /// <summary>
        /// Three patients: a planning CT with a dose, a CBCT with a dose, and a planning CT with
        /// none. Require a dose and "Series description contains: planning" keep the first only;
        /// the plan skips the other two, each with its reason, on both paths.
        /// </summary>
        private static Task<ParityRun> SeriesFiltersRun() => ExportParity.RunOnceAsync(
            "series-filters", Cohorts.ThreeSeriesForTheSeriesFilters, new ExportToggles
            {
                RequireDose = true,
                SeriesDescriptionFilter = "planning",
                ExpectedSkipped = 2,
            });

        [AvaloniaFact]
        public async Task Series_filters_write_the_same_files()
        {
            var run = await SeriesFiltersRun();
            AssertSameTree(run);
            Assert.All(run.Gui.Tree.Keys, path => Assert.StartsWith("FILTER01/", path));
        }

        [AvaloniaFact]
        public async Task Series_filters_write_the_same_manifest()
            => AssertSameManifest(await SeriesFiltersRun());

        /// <summary>The mask files of an export, relative to its root, in ordinal order.</summary>
        private static List<string> Masks(ExportSnapshot snapshot) =>
            snapshot.Tree.Keys
                .Where(k => k.Contains("/masks/") && k.EndsWith(".nii.gz", StringComparison.Ordinal))
                .Select(k => k.Substring(0, k.IndexOf('/') + 1) + "masks/" + k.Substring(k.LastIndexOf('/') + 1))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        // ── comparison ─────────────────────────────────────────────────────────────────────

        private const string ManifestFileName = ExportManifestService.DefaultFileName;

        private void AssertSameTree(ParityRun run)
        {
            LogHashes(run);
            _out.WriteLine("GUI export:\n" + ExportParity.Describe(run.Gui.Tree));
            _out.WriteLine("Service export:\n" + ExportParity.Describe(run.Service.Tree));
            _out.WriteLine("GUI log:\n" + run.Gui.Log);
            _out.WriteLine("Service plan and log:\n" + run.Service.Log);

            Assert.NotEmpty(run.Service.Tree);
            var differences = ExportParity.DiffTrees(run.Gui.Tree, run.Service.Tree);
            Assert.True(differences.Count == 0,
                $"the GUI and the service wrote different trees ({differences.Count} difference(s)):\n  " +
                string.Join("\n  ", differences));
        }

        private void AssertSameManifest(ParityRun run)
            => AssertSameBytes(run, ManifestFileName, s => s.Manifest);

        /// <summary>
        /// The manifest's rows and cells read by column name: every identifier, spacing and
        /// volume, without the ROI columns' order. A weaker claim than <see cref="AssertSameManifest"/>,
        /// which stays the one W3-D.2 has to make pass.
        /// </summary>
        private void AssertSameManifestCells(ParityRun run)
        {
            LogHashes(run);
            Assert.True(run.Gui.Manifest != null, $"the GUI wrote no {ManifestFileName}");
            Assert.True(run.Service.Manifest != null, $"the service wrote no {ManifestFileName}");
            string gui = ExportParity.ByColumnName(run.Gui.Manifest);
            string service = ExportParity.ByColumnName(run.Service.Manifest);
            _out.WriteLine($"GUI {ManifestFileName} by column name:\n{gui}");
            _out.WriteLine($"Service {ManifestFileName} by column name:\n{service}");
            Assert.Equal(service, gui);
        }

        private void AssertSameBytes(ParityRun run, string what, Func<ExportSnapshot, byte[]> pick)
        {
            LogHashes(run);
            byte[] gui = pick(run.Gui), service = pick(run.Service);
            Assert.True(gui != null, $"the GUI wrote no {what}");
            Assert.True(service != null, $"the service wrote no {what}");
            _out.WriteLine($"GUI {what}:\n{Encoding.UTF8.GetString(gui)}");
            _out.WriteLine($"Service {what}:\n{Encoding.UTF8.GetString(service)}");
            Assert.True(gui.SequenceEqual(service),
                $"the GUI and the service wrote different {what}:\n--- GUI\n{Encoding.UTF8.GetString(gui)}\n--- service\n{Encoding.UTF8.GetString(service)}");
        }

        private void LogHashes(ParityRun run)
            => _out.WriteLine($"parity hash: GUI {run.Gui.Hash}; service {run.Service.Hash}");

        // ── cohort synthesis ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Synthetic inputs, written with Core.Tests' fo-dicom helpers plus a pixel-bearing
        /// RT-DOSE. Every image series is a 16 × 16 CT at 1 mm whose origin is (0, 0, 0), so a
        /// contour's mm coordinates are its voxel indices.
        ///
        /// Study, series and frame UIDs are fixed rather than generated, so each case is the same
        /// input on every run: the scanner orders series by SeriesDate then SeriesInstanceUID, and
        /// that order decides which series a folder collision lets win and which structure set
        /// and dose are "the first". Only the per-instance SOP UIDs, which no export writes, vary.
        /// </summary>
        internal static class Cohorts
        {
            /// <remarks>
            /// Contours sit on z = 2 and z = 3 mm only: resampled to 1.5 mm, the 4-slice CT keeps
            /// planes 0, 1.5 and 3, whose nearest source slices are 0, 2 and 3, so every mask
            /// survives case 2 with voxels in it rather than as an all-zero volume.
            /// </remarks>
            public static void TwoPatientsWithStructuresAndDose(string input)
            {
                WritePatient(Path.Combine(input, "PARITY01"), "PARITY01", uidBase: 1100,
                    Roi("Target", 3, 10, 2), Roi("Avoid", 2, 6, 3), Roi("Body", 1, 14, 2));
                WritePatient(Path.Combine(input, "PARITY02"), "PARITY02", uidBase: 1200,
                    Roi("Body", 1, 14, 3), Roi("Cord", 7, 9, 2), Roi("Target", 4, 11, 3));
            }

            /// <remarks>Series A (UID 2.25.3110) sorts before series B (2.25.3120): their SeriesDates tie.</remarks>
            public static void TwoSeriesWithEqualDateAndDescription(string input)
            {
                const string patientId = "PARITY03";
                string dir = Path.Combine(input, patientId);
                string study = Uid(3100);

                // Different lengths, so whichever image lands in a shared folder is identifiable.
                foreach (var (name, uidBase, slices, rois) in new[]
                         {
                             ("A", 3110, 4, new[] { Roi("Target", 3, 10, 1), Roi("Avoid", 2, 6, 2) }),
                             ("B", 3120, 6, new[] { Roi("Target", 4, 11, 3), Roi("Cord", 7, 9, 4) }),
                         })
                {
                    string ct = Uid(uidBase), frame = Uid(uidBase + 1);
                    string ctDir = DicomTestData.WriteCtSeriesWithPixels(Path.Combine(dir, "CT_" + name), ct, frame,
                        sliceCount: slices, size: 16, studyUid: study, patientId: patientId);
                    StampSeriesDate(ctDir, "20240115");
                    DicomTestData.WriteRtStructWithContours(Path.Combine(dir, "RS_" + name), "rtstruct.dcm",
                        study, Uid(uidBase + 2), frame, rois, patientId: patientId, referencedSeriesUid: ct);
                }
            }

            /// <remarks>
            /// Structure set 1 (Target, Avoid; UID 2.25.4120) sorts before structure set 2 (Body,
            /// Cord; 2.25.4130), and "Plan A dose" (2.25.4140) before "Plan B dose" (2.25.4150), so
            /// set 1 and Plan A are each path's "first".
            /// </remarks>
            public static void OneSeriesWithTwoStructureSetsAndTwoDoses(string input)
            {
                const string patientId = "PARITY04";
                string dir = Path.Combine(input, patientId);
                string study = Uid(4100), ct = Uid(4110), frame = Uid(4111);

                DicomTestData.WriteCtSeriesWithPixels(Path.Combine(dir, "CT"), ct, frame,
                    sliceCount: 4, size: 16, studyUid: study, patientId: patientId);
                DicomTestData.WriteRtStructWithContours(Path.Combine(dir, "RS_1"), "rtstruct.dcm",
                    study, Uid(4120), frame,
                    new[] { Roi("Target", 3, 10, 1), Roi("Avoid", 2, 6, 2) },
                    patientId: patientId, referencedSeriesUid: ct);
                DicomTestData.WriteRtStructWithContours(Path.Combine(dir, "RS_2"), "rtstruct.dcm",
                    study, Uid(4130), frame,
                    new[] { Roi("Body", 1, 14, 1), Roi("Cord", 7, 9, 2) },
                    patientId: patientId, referencedSeriesUid: ct);
                WriteDose(Path.Combine(dir, "RD_1"), patientId, study, Uid(4140), frame, "Plan A dose", seed: 17);
                WriteDose(Path.Combine(dir, "RD_2"), patientId, study, Uid(4150), frame, "Plan B dose", seed: 29);
            }

            /// <remarks>
            /// Only FILTER01 passes both "Series description contains: planning" and Require a
            /// dose: FILTER02 is a CBCT, and FILTER03 has no dose.
            /// </remarks>
            public static void ThreeSeriesForTheSeriesFilters(string input)
            {
                foreach (var (patientId, uidBase, description, dose) in new[]
                         {
                             ("FILTER01", 7100, "Planning CT", true),
                             ("FILTER02", 7200, "CBCT day 1", true),
                             ("FILTER03", 7300, "Planning CT", false),
                         })
                {
                    WritePatient(Path.Combine(input, patientId), patientId, uidBase,
                        description, dose, Roi("Target", 3, 10, 2), Roi("Body", 1, 14, 3));
                }
            }

            /// <summary>A CT, a structure set referencing it by SeriesInstanceUID, and one dose on its frame.</summary>
            private static void WritePatient(string dir, string patientId, int uidBase, params KeyValuePair<string, double[]>[] rois)
                => WritePatient(dir, patientId, uidBase, null, true, rois);

            /// <summary>
            /// As above, with the CT's SeriesDescription set to <paramref name="description"/> (null
            /// keeps the writer's "CT series"), and the dose left out unless <paramref name="dose"/>.
            /// </summary>
            private static void WritePatient(string dir, string patientId, int uidBase, string description, bool dose,
                params KeyValuePair<string, double[]>[] rois)
            {
                string study = Uid(uidBase), ct = Uid(uidBase + 10), frame = Uid(uidBase + 11);
                string ctDir = DicomTestData.WriteCtSeriesWithPixels(Path.Combine(dir, "CT"), ct, frame,
                    sliceCount: 4, size: 16, studyUid: study, patientId: patientId);
                if (description != null)
                    Stamp(ctDir, DicomTag.SeriesDescription, description);
                DicomTestData.WriteRtStructWithContours(Path.Combine(dir, "RS"), "rtstruct.dcm",
                    study, Uid(uidBase + 20), frame, rois, patientId: patientId, referencedSeriesUid: ct);
                if (dose)
                    WriteDose(Path.Combine(dir, "RD"), patientId, study, Uid(uidBase + 30), frame, "Plan dose", seed: 17);
            }

            /// <summary>A fixed UID under the 2.25 (UUID-derived) root.</summary>
            private static string Uid(int n) => "2.25." + n.ToString(CultureInfo.InvariantCulture);

            private static KeyValuePair<string, double[]> Roi(string name, double min, double max, double z) =>
                new KeyValuePair<string, double[]>(name, DicomTestData.SquareContour(min, max, z));

            private static void StampSeriesDate(string ctDir, string date) => Stamp(ctDir, DicomTag.SeriesDate, date);

            private static void Stamp(string ctDir, DicomTag tag, string value)
            {
                foreach (string slice in Directory.GetFiles(ctDir, "*.dcm"))
                {
                    var file = DicomFile.Open(slice);
                    file.Dataset.AddOrUpdate(tag, value);
                    file.Save(slice);
                }
            }

            /// <summary>
            /// A 6 × 5 × 3 multi-frame 16-bit RT-DOSE in GY on its own grid inside the CT, linked
            /// by frame of reference; <paramref name="seed"/> varies the stored values so two doses
            /// write different volumes.
            /// </summary>
            private static void WriteDose(string dir, string patientId, string studyUid, string seriesUid,
                string frameUid, string description, int seed)
            {
                const int columns = 6, rows = 5, frames = 3;
                var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                {
                    { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
                    { DicomTag.SOPInstanceUID, DicomTestData.NewUid() },
                    { DicomTag.PatientID, patientId },
                    { DicomTag.StudyInstanceUID, studyUid },
                    { DicomTag.SeriesInstanceUID, seriesUid },
                    { DicomTag.Modality, "RTDOSE" },
                    { DicomTag.FrameOfReferenceUID, frameUid },
                    { DicomTag.SeriesDescription, description },
                    { DicomTag.DoseUnits, "GY" },
                    { DicomTag.DoseType, "PHYSICAL" },
                    { DicomTag.DoseSummationType, "PLAN" },
                    { DicomTag.SamplesPerPixel, (ushort)1 },
                    { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                    { DicomTag.BitsAllocated, (ushort)16 },
                    { DicomTag.BitsStored, (ushort)16 },
                    { DicomTag.HighBit, (ushort)15 },
                    { DicomTag.PixelRepresentation, (ushort)0 },
                    { DicomTag.Rows, (ushort)rows },
                    { DicomTag.Columns, (ushort)columns },
                    { DicomTag.NumberOfFrames, frames.ToString(CultureInfo.InvariantCulture) },
                    { DicomTag.FrameIncrementPointer, DicomTag.GridFrameOffsetVector },
                    { DicomTag.DoseGridScaling, "0.001" },
                };
                ds.Add(DicomTag.ImagePositionPatient, "1.5", "2.5", "0");
                ds.Add(DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0");
                ds.Add(DicomTag.PixelSpacing, "2.5", "2");
                ds.Add(DicomTag.SliceThickness, "1.5");
                ds.Add(DicomTag.GridFrameOffsetVector, "0", "1.5", "3");

                var stored = new ushort[columns * rows * frames];
                for (int i = 0; i < stored.Length; i++)
                    stored[i] = (ushort)(i * 1009 + seed);
                byte[] bytes = new byte[stored.Length * 2];
                Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
                ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));

                Directory.CreateDirectory(dir);
                new DicomFile(ds).Save(Path.Combine(dir, "RD.dcm"));
            }
        }
    }
}
