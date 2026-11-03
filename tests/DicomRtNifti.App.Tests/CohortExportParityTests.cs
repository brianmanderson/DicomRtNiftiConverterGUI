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
    /// W3-D.1: does <c>CohortExportService.BuildPlan</c> + <c>ExecuteAsync</c> write what the GUI's
    /// Convert Selected writes? The forward window re-implements output naming, anonymization, the
    /// export loop and the manifest (MainViewModel.ExecuteConvertAsync) that the service already
    /// owns; before W3-D.2 moves the window onto the service, this pins where the two agree and
    /// names where they do not.
    ///
    /// Each case synthesizes a cohort, exports it once through each path
    /// (<see cref="ExportParity"/>), and compares, byte for byte: every file and folder under the
    /// output root with its SHA-256, the manifest CSV, and the anonymization key; and the manifest
    /// once more read by column name. Each aspect is its own test, so a case that disagrees on one
    /// still pins the others.
    ///
    /// A disagreement is a finding, filed for W3-D.2 to resolve in favour of the service, and its
    /// test is skipped with the issue in the reason. It is never written down here as the expected
    /// result: every assertion below says "the same", and W3-D.2 is done when the skips can go.
    ///
    /// Lives in App.Tests because only this project reaches both paths: it references the App
    /// (MainViewModel, driven headless through the real launcher) and, through it, Core
    /// (CohortExportService), and it already compiles in Core.Tests' DicomTestData. Core.Tests
    /// cannot reference the App without pulling Avalonia into Core's test lane.
    /// </summary>
    public class CohortExportParityTests
    {
        // The findings at main @ 5d8e873. A fact that would fail on one is skipped citing it,
        // never rewritten to expect the difference; W3-D.2 (#77) resolves each one in favour of
        // the service and removes its skips.
        private const string Issues = "https://github.com/brianmanderson/DicomRtNiftiConverterGUI/issues/";
        private const string CollidingFoldersFinding =
            "Finding F-c, " + Issues + "50: Convert Selected writes both series into one folder. W3-D.2 (#77) removes this skip.";
        private const string FirstDoseOnlyFinding =
            "Finding " + Issues + "91: Convert Selected exports only the first linked RTDOSE. W3-D.2 (#77) removes this skip.";
        private const string RoiColumnOrderFinding =
            "Finding " + Issues + "92: Convert Selected orders the manifest's ROI columns by hash-table enumeration, " +
            "the service by name. W3-D.2 (#77) removes this skip.";

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

        [AvaloniaFact(Skip = RoiColumnOrderFinding)]
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

        [AvaloniaFact(Skip = RoiColumnOrderFinding)]
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
        /// its own structure set. Both plan the folder "&lt;date&gt;_CT series": the service suffixes
        /// each with a hash of its SeriesInstanceUID, the GUI writes the second into the first
        /// (F-c). The manifests' cells still agree: both list both series with their own volumes.
        /// </summary>
        private static Task<ParityRun> CollidingRun() => ExportParity.RunOnceAsync(
            "colliding-folders", Cohorts.TwoSeriesWithEqualDateAndDescription, new ExportToggles());

        [AvaloniaFact(Skip = CollidingFoldersFinding)]
        public async Task Two_series_with_equal_date_and_description_write_the_same_files()
            => AssertSameTree(await CollidingRun());

        [AvaloniaFact(Skip = RoiColumnOrderFinding)]
        public async Task Two_series_with_equal_date_and_description_write_the_same_manifest()
            => AssertSameManifest(await CollidingRun());

        [AvaloniaFact]
        public async Task Two_series_with_equal_date_and_description_write_the_same_manifest_cells()
            => AssertSameManifestCells(await CollidingRun());

        // ── case 4: one series with two structure sets and two doses ──────────────────────

        /// <summary>
        /// One CT carrying two structure sets (different ROIs) and two doses (different
        /// descriptions), all linked to it. Both paths export the first structure set only, which
        /// agrees (choosing among them is W4-C's); the service exports both doses and the GUI the
        /// first.
        /// </summary>
        private static Task<ParityRun> TwoOfEachRun() => ExportParity.RunOnceAsync(
            "two-structure-sets-two-doses", Cohorts.OneSeriesWithTwoStructureSetsAndTwoDoses, new ExportToggles());

        [AvaloniaFact(Skip = FirstDoseOnlyFinding)]
        public async Task Two_structure_sets_and_two_doses_write_the_same_files()
            => AssertSameTree(await TwoOfEachRun());

        [AvaloniaFact(Skip = RoiColumnOrderFinding)]
        public async Task Two_structure_sets_and_two_doses_write_the_same_manifest()
            => AssertSameManifest(await TwoOfEachRun());

        [AvaloniaFact]
        public async Task Two_structure_sets_and_two_doses_write_the_same_manifest_cells()
            => AssertSameManifestCells(await TwoOfEachRun());

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
        private static class Cohorts
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

            /// <summary>A CT, a structure set referencing it by SeriesInstanceUID, and one dose on its frame.</summary>
            private static void WritePatient(string dir, string patientId, int uidBase, params KeyValuePair<string, double[]>[] rois)
            {
                string study = Uid(uidBase), ct = Uid(uidBase + 10), frame = Uid(uidBase + 11);
                DicomTestData.WriteCtSeriesWithPixels(Path.Combine(dir, "CT"), ct, frame,
                    sliceCount: 4, size: 16, studyUid: study, patientId: patientId);
                DicomTestData.WriteRtStructWithContours(Path.Combine(dir, "RS"), "rtstruct.dcm",
                    study, Uid(uidBase + 20), frame, rois, patientId: patientId, referencedSeriesUid: ct);
                WriteDose(Path.Combine(dir, "RD"), patientId, study, Uid(uidBase + 30), frame, "Plan dose", seed: 17);
            }

            /// <summary>A fixed UID under the 2.25 (UUID-derived) root.</summary>
            private static string Uid(int n) => "2.25." + n.ToString(CultureInfo.InvariantCulture);

            private static KeyValuePair<string, double[]> Roi(string name, double min, double max, double z) =>
                new KeyValuePair<string, double[]>(name, DicomTestData.SquareContour(min, max, z));

            private static void StampSeriesDate(string ctDir, string date)
            {
                foreach (string slice in Directory.GetFiles(ctDir, "*.dcm"))
                {
                    var file = DicomFile.Open(slice);
                    file.Dataset.AddOrUpdate(DicomTag.SeriesDate, date);
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
