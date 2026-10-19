using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Core.Tests;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The GUI half of W2-E's PHI audit (#53). Convert Selected writes the forward metadata.json
    /// through its own view-model path rather than CohortExportService, so the Core fix did not
    /// reach it: under Anonymize it wrote the selected tags verbatim. These tests run the real
    /// DICOM → NIfTI window over a synthetic two-patient cohort carrying every identifier the
    /// W2-E audit uses (name, ID, birth and study dates, accession, referring physician,
    /// institution, every UID, a private element holding the name and ID, input folders named
    /// after the patient), with identifying tags selected for the sidecar in all three sections.
    ///
    /// Anonymized, every file the export writes — NIfTI headers and payloads (also inside the
    /// gzip stream), metadata.json, the manifest, file and folder names — plus the log the convert
    /// wrote and the GUI state it saved is searched for every source identifier, as UTF-8 and
    /// UTF-16. AnonymizationKey.json is exempt: it is the re-identification key, and the test
    /// asserts it holds the identifiers, so the audit is known to look for the right values.
    /// Not anonymized, the same tags are written verbatim and the same search finds them, which
    /// is the search's positive control.
    /// </summary>
    public class AnonymizedExportMetadataTests
    {
        private const string Salt = "gui-phi-audit-salt";
        private const string KeyFileName = "AnonymizationKey.json";

        private readonly ITestOutputHelper _out;

        public AnonymizedExportMetadataTests(ITestOutputHelper output) => _out = output;

        // Identifying tags a user plausibly ticks in the picker, next to the ones the sidecar
        // exists for: the W2-E audit's selection.
        private static readonly List<string> ImageKeywords = new List<string>
        {
            "PatientName", "PatientID", "PatientBirthDate", "PatientSex", "StudyDate", "SeriesDate",
            "StudyTime", "AccessionNumber", "ReferringPhysicianName", "InstitutionName",
            "StudyInstanceUID", "SeriesInstanceUID", "SOPInstanceUID", "FrameOfReferenceUID",
            "SOPClassUID", "Modality", "SeriesDescription", "PixelSpacing",
            MetadataTagCatalog.VoxelSizeKey, MetadataTagCatalog.ImageDimensionsKey,
        };

        private static readonly List<string> StructureKeywords = new List<string>
        {
            "PatientName", "PatientID", "StructureSetLabel", "SeriesInstanceUID", "StudyDate",
            MetadataTagCatalog.RoiNamesKey, MetadataTagCatalog.RoiCountKey,
        };

        private static readonly List<string> DoseKeywords = new List<string>
        {
            "PatientName", "PatientBirthDate", "DoseUnits", "DoseType", "SOPInstanceUID",
            MetadataTagCatalog.MaxDoseKey, MetadataTagCatalog.DoseVoxelSizeKey,
        };

        [AvaloniaFact]
        public async Task Anonymized_convert_selected_writes_no_source_identifier()
        {
            using (new UserSettingsGuard())
            using (var cohort = new PhiCohort())
            using (var launcher = new LauncherSession())
            {
                string convertLog = await ConvertSelectedAsync(launcher, cohort, anonymize: true);
                string keyPath = Path.Combine(cohort.OutputFolder, KeyFileName);

                // The audit is only as good as the identifiers it looks for: the key, which maps
                // each source identifier to its hash, must hold the ones the export was given.
                Assert.True(File.Exists(keyPath), "AnonymizationKey.json missing");
                string keyText = File.ReadAllText(keyPath);
                foreach (var p in cohort.Patients)
                {
                    Assert.Contains(p.PatientId, keyText);
                    Assert.Contains(p.StudyUid, keyText);
                    Assert.Contains(p.CtSeriesUid, keyText);
                }

                var identifiers = cohort.Patients.SelectMany(p => p.Identifiers()).ToList();
                var leaks = new List<string>();
                AuditTree(cohort.OutputFolder, keyPath, identifiers, leaks);
                AuditText("convert log", convertLog, identifiers, leaks);
                string stateFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DicomToNifti");
                foreach (string name in new[] { "settings.json", "gui_state.json" })
                {
                    string path = Path.Combine(stateFolder, name);
                    if (File.Exists(path))
                        AuditText(name, File.ReadAllText(path), identifiers, leaks);
                }

                var files = Directory.GetFiles(cohort.OutputFolder, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(cohort.OutputFolder.Length + 1).Replace('\\', '/'))
                    .OrderBy(f => f, StringComparer.Ordinal);
                _out.WriteLine("Audited files:\n  " + string.Join("\n  ", files));

                var distinct = leaks.Distinct().ToList();
                Assert.True(distinct.Count == 0,
                    $"{distinct.Count} source identifier occurrence(s) in the anonymized GUI export:\n  " +
                    string.Join("\n  ", distinct));

                // The sidecar keeps what it is for: non-identifying values, computed values, and
                // the export's own hashes in place of the source identifiers, the same ones the
                // folders and the key use, so it still joins to them.
                var key = AnonymizationService.LoadKeyFile(keyPath);
                var sidecars = Directory.GetFiles(cohort.OutputFolder, "metadata.json", SearchOption.AllDirectories);
                Assert.Equal(cohort.Patients.Count, sidecars.Length);
                foreach (var p in cohort.Patients)
                {
                    string patientHash = key.Patients[p.PatientId];
                    string studyHash = key.Studies[p.StudyUid];
                    string seriesHash = key.Series[p.CtSeriesUid];
                    string sidecar = Path.Combine(cohort.OutputFolder, patientHash, studyHash, seriesHash, "metadata.json");
                    Assert.True(File.Exists(sidecar), $"no metadata.json in the export folder of {p.PatientId}");

                    var json = JObject.Parse(File.ReadAllText(sidecar));
                    var image = (JObject)json["ImageAttributes"];
                    Assert.Equal(patientHash, (string)image[Friendly("PatientID")]);
                    Assert.Equal(studyHash, (string)image[Friendly("StudyInstanceUID")]);
                    Assert.Equal(seriesHash, (string)image[Friendly("SeriesInstanceUID")]);
                    foreach (string removed in new[]
                             {
                                 "PatientName", "PatientBirthDate", "StudyDate", "SeriesDate", "StudyTime",
                                 "AccessionNumber", "ReferringPhysicianName", "InstitutionName",
                                 "SOPInstanceUID", "FrameOfReferenceUID",
                             })
                        Assert.True(image[Friendly(removed)]?.Type == JTokenType.Null, $"ImageAttributes {removed} is not null");
                    Assert.Equal("CT", (string)image[Friendly("Modality")]);
                    Assert.Equal("CT series", (string)image[Friendly("SeriesDescription")]);
                    Assert.Equal(DicomUID.CTImageStorage.UID, (string)image[Friendly("SOPClassUID")]);
                    Assert.Equal("O", (string)image[Friendly("PatientSex")]);
                    Assert.Equal(JTokenType.Array, image[Friendly(MetadataTagCatalog.VoxelSizeKey)].Type);

                    var structure = (JObject)json["StructureAttributes"];
                    Assert.Equal(patientHash, (string)structure[Friendly("PatientID")]);
                    Assert.Equal(key.Series[p.StructSeriesUid], (string)structure[Friendly("SeriesInstanceUID")]);
                    Assert.Equal(JTokenType.Null, structure[Friendly("PatientName")].Type);
                    Assert.Equal(JTokenType.Null, structure[Friendly("StudyDate")].Type);
                    Assert.Equal("TEST", (string)structure[Friendly("StructureSetLabel")]);
                    Assert.Equal(2L, (long)structure[Friendly(MetadataTagCatalog.RoiCountKey)]);

                    var dose = (JObject)json["DoseAttributes"];
                    Assert.Equal(JTokenType.Null, dose[Friendly("PatientName")].Type);
                    Assert.Equal(JTokenType.Null, dose[Friendly("PatientBirthDate")].Type);
                    Assert.Equal(JTokenType.Null, dose[Friendly("SOPInstanceUID")].Type);
                    Assert.Equal("GY", (string)dose[Friendly("DoseUnits")]);
                    Assert.Equal("PHYSICAL", (string)dose[Friendly("DoseType")]);
                    Assert.Equal(JTokenType.Float, dose[Friendly(MetadataTagCatalog.MaxDoseKey)].Type);
                }
            }
        }

        [AvaloniaFact]
        public async Task Convert_selected_without_anonymize_writes_metadata_tags_verbatim()
        {
            using (new UserSettingsGuard())
            using (var cohort = new PhiCohort())
            using (var launcher = new LauncherSession())
            {
                await ConvertSelectedAsync(launcher, cohort, anonymize: false);
                Assert.False(File.Exists(Path.Combine(cohort.OutputFolder, KeyFileName)),
                    "a non-anonymized export wrote an anonymization key");

                var sidecars = Directory.GetFiles(cohort.OutputFolder, "metadata.json", SearchOption.AllDirectories);
                Assert.Equal(cohort.Patients.Count, sidecars.Length);
                foreach (var p in cohort.Patients)
                {
                    string sidecar = Assert.Single(sidecars, s =>
                        s.Substring(cohort.OutputFolder.Length + 1).StartsWith(p.PatientId + Path.DirectorySeparatorChar));
                    var json = JObject.Parse(File.ReadAllText(sidecar));

                    var image = (JObject)json["ImageAttributes"];
                    Assert.Equal(p.PatientId, (string)image[Friendly("PatientID")]);
                    Assert.Equal(p.PatientName, (string)image[Friendly("PatientName")]);
                    Assert.Equal(p.BirthDate, (string)image[Friendly("PatientBirthDate")]);
                    Assert.Equal(p.StudyDate, (string)image[Friendly("StudyDate")]);
                    Assert.Equal(p.AccessionNumber, (string)image[Friendly("AccessionNumber")]);
                    Assert.Equal(p.ReferringPhysician, (string)image[Friendly("ReferringPhysicianName")]);
                    Assert.Equal(p.InstitutionName, (string)image[Friendly("InstitutionName")]);
                    Assert.Equal(p.StudyUid, (string)image[Friendly("StudyInstanceUID")]);
                    Assert.Equal(p.CtSeriesUid, (string)image[Friendly("SeriesInstanceUID")]);
                    Assert.Equal(p.FrameUid, (string)image[Friendly("FrameOfReferenceUID")]);
                    Assert.Contains((string)image[Friendly("SOPInstanceUID")], p.SopInstanceUids);

                    var structure = (JObject)json["StructureAttributes"];
                    Assert.Equal(p.PatientName, (string)structure[Friendly("PatientName")]);
                    Assert.Equal(p.StructSeriesUid, (string)structure[Friendly("SeriesInstanceUID")]);

                    var dose = (JObject)json["DoseAttributes"];
                    Assert.Equal(p.PatientName, (string)dose[Friendly("PatientName")]);
                    Assert.Equal(p.BirthDate, (string)dose[Friendly("PatientBirthDate")]);

                    // The search the anonymized test relies on finds these very values here, so
                    // its silence there means absence, not blindness.
                    var leaks = new List<string>();
                    AuditBytes("metadata.json", File.ReadAllBytes(sidecar), p.Identifiers().ToList(), leaks);
                    foreach (string label in new[] { "PatientID", "PatientName", "PatientBirthDate", "StudyDate",
                                 "AccessionNumber", "InstitutionName", "StudyInstanceUID", "CT SeriesInstanceUID" })
                        Assert.Contains(leaks, l => l.StartsWith($"metadata.json: {label} '", StringComparison.Ordinal));
                }
            }
        }

        /// <summary>
        /// Seeds the tag picker's choices the way the picker persists them, opens DICOM → NIfTI
        /// from the launcher, scans the cohort and runs Convert Selected over every series.
        /// Returns the part of the log the convert wrote.
        /// </summary>
        private async Task<string> ConvertSelectedAsync(LauncherSession launcher, PhiCohort cohort, bool anonymize)
        {
            launcher.Services.Settings.SaveSettings(new AppSettings
            {
                HashSalt = Salt,
                ExportDicomMetadata = true,
                MetadataImageTagKeywords = ImageKeywords,
                MetadataStructureTagKeywords = StructureKeywords,
                MetadataDoseTagKeywords = DoseKeywords,
            });

            var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
            try
            {
                vm.InputFolder = cohort.InputFolder;
                vm.OutputFolder = cohort.OutputFolder;
                vm.ExportImages = true;
                vm.IncludeStructures = true;
                vm.IncludeDose = true;
                vm.OnlyExportSpecificRois = false;
                vm.SpecifyOutputSpacing = false;
                vm.AnonymizeExport = anonymize;
                vm.ExportDicomMetadata = true;

                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Scan complete.", vm.StatusText);
                Assert.Equal(cohort.Patients.Count, vm.Patients.Count);
                foreach (var patient in vm.Patients)
                {
                    var series = Assert.Single(Assert.Single(patient.Studies).ImageSeries);
                    Assert.True(series.HasLinkedRtStruct, "the RTSTRUCT was not linked to the CT series");
                    Assert.True(series.HasLinkedRtDose, "the RTDOSE was not linked to the CT series");
                }

                vm.AllPatientsSelected = true;
                int logStart = vm.LogText.Length;
                Assert.True(vm.ConvertSelectedCommand.CanExecute(null));
                await vm.ConvertSelectedCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                // The log, not StatusText: the last "Wrote metadata.json" progress message is
                // posted through Progress<T> and lands after "Conversion complete." (#59).
                string convertLog = vm.LogText.Substring(logStart);
                _out.WriteLine(convertLog);
                Assert.DoesNotContain("Conversion error", convertLog);
                Assert.Contains($"Conversion complete. {cohort.Patients.Count} series exported to", convertLog);
                Assert.DoesNotContain("metadata.json failed", convertLog);
                Assert.DoesNotContain("verbatim", convertLog);
                return convertLog;
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static string Friendly(string keyword) => MetadataTagCatalog.FriendlyName(keyword);

        // ── cohort synthesis ───────────────────────────────────────────────────────────────

        /// <summary>One synthetic patient's identifiers, distinctive enough that a hit is never chance.</summary>
        private sealed class SyntheticPatient
        {
            public string PatientId;
            public string FamilyName;
            public string GivenName;
            public string BirthDate;     // DA, yyyyMMdd
            public string StudyDate;     // DA, yyyyMMdd
            public string AccessionNumber;
            public string ReferringFamily;
            public string ReferringGiven;
            public string InstitutionName;

            public readonly string StudyUid = DicomTestData.NewUid();
            public readonly string FrameUid = DicomTestData.NewUid();
            public readonly string CtSeriesUid = DicomTestData.NewUid();
            public readonly string StructSeriesUid = DicomTestData.NewUid();
            public readonly string DoseSeriesUid = DicomTestData.NewUid();
            public readonly List<string> SopInstanceUids = new List<string>();

            public string PatientName => FamilyName + "^" + GivenName;
            public string ReferringPhysician => ReferringFamily + "^" + ReferringGiven;

            /// <summary>Every value whose appearance in the export would be a leak, with a label.</summary>
            public IEnumerable<KeyValuePair<string, string>> Identifiers()
            {
                yield return Pair("PatientID", PatientId);
                yield return Pair("PatientName", PatientName);
                yield return Pair("PatientName family", FamilyName);
                yield return Pair("PatientName given", GivenName);
                yield return Pair("PatientBirthDate", BirthDate);
                yield return Pair("PatientBirthDate (ISO)", Iso(BirthDate));
                yield return Pair("StudyDate", StudyDate);
                yield return Pair("StudyDate (ISO)", Iso(StudyDate));
                yield return Pair("AccessionNumber", AccessionNumber);
                yield return Pair("ReferringPhysicianName family", ReferringFamily);
                yield return Pair("ReferringPhysicianName given", ReferringGiven);
                yield return Pair("InstitutionName", InstitutionName);
                yield return Pair("StudyInstanceUID", StudyUid);
                yield return Pair("FrameOfReferenceUID", FrameUid);
                yield return Pair("CT SeriesInstanceUID", CtSeriesUid);
                yield return Pair("RTSTRUCT SeriesInstanceUID", StructSeriesUid);
                yield return Pair("RTDOSE SeriesInstanceUID", DoseSeriesUid);
                foreach (var sop in SopInstanceUids)
                    yield return Pair("SOPInstanceUID", sop);
            }

            private static KeyValuePair<string, string> Pair(string label, string value) =>
                new KeyValuePair<string, string>(label, value);

            private static string Iso(string da) => $"{da.Substring(0, 4)}-{da.Substring(4, 2)}-{da.Substring(6, 2)}";
        }

        /// <summary>
        /// Two patients, each a 16x16x4 CT, a two-ROI RTSTRUCT and a multi-frame RT-DOSE on its
        /// own grid, under one temp root that is deleted on dispose. The identifiers are W2-E's.
        /// </summary>
        private sealed class PhiCohort : IDisposable
        {
            public string Root { get; }
            public string InputFolder { get; }
            public string OutputFolder { get; }
            public IReadOnlyList<SyntheticPatient> Patients { get; }

            public PhiCohort()
            {
                Root = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));
                InputFolder = Path.Combine(Root, "dicom");
                OutputFolder = Path.Combine(Root, "out");
                Directory.CreateDirectory(InputFolder);
                Directory.CreateDirectory(OutputFolder);

                Patients = new[]
                {
                    new SyntheticPatient
                    {
                        PatientId = "MRN7Q4X2KZ",
                        FamilyName = "Quillfeather",
                        GivenName = "Ottoline",
                        BirthDate = "19370412",
                        StudyDate = "20180923",
                        AccessionNumber = "ACC5517XQV",
                        ReferringFamily = "Hollowbrook",
                        ReferringGiven = "Edmundine",
                        InstitutionName = "Wexmoor Regional Cancer Centre",
                    },
                    new SyntheticPatient
                    {
                        PatientId = "MRN3J8W5RT",
                        FamilyName = "Brindlecombe",
                        GivenName = "Peregrine",
                        BirthDate = "19520730",
                        StudyDate = "20190611",
                        AccessionNumber = "ACC2290LMB",
                        ReferringFamily = "Fennimore",
                        ReferringGiven = "Rosalind",
                        InstitutionName = "Ashgrove University Hospital",
                    },
                };
                foreach (var p in Patients)
                    WritePatient(InputFolder, p);
            }

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { /* a straggling handle on Windows; the temp folder is per-run anyway */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void WritePatient(string input, SyntheticPatient p)
        {
            // Named after the patient, as a PACS or TPS export tree usually is, so a source path
            // echoed into any artifact or message is caught too.
            string patientDir = Path.Combine(input, p.PatientId + "_" + p.FamilyName + "_" + p.GivenName);

            string ctDir = DicomTestData.WriteCtSeriesWithPixels(
                Path.Combine(patientDir, "CT"), p.CtSeriesUid, p.FrameUid,
                sliceCount: 4, size: 16, studyUid: p.StudyUid, patientId: p.PatientId);
            foreach (var slice in Directory.GetFiles(ctDir, "*.dcm"))
                StampIdentifiers(slice, p);

            string rs = DicomTestData.WriteRtStructWithContours(
                Path.Combine(patientDir, "RTSTRUCT"), "RS.dcm", p.StudyUid, p.StructSeriesUid, p.FrameUid,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("GTV", DicomTestData.SquareContour(4, 9, 1)),
                    new KeyValuePair<string, double[]>("Body", DicomTestData.SquareContour(1, 14, 2)),
                },
                patientId: p.PatientId, referencedSeriesUid: p.CtSeriesUid);
            StampIdentifiers(rs, p);

            StampIdentifiers(WriteDose(Path.Combine(patientDir, "RTDOSE"), p), p);
        }

        /// <summary>
        /// A 6x5x3 multi-frame 16-bit RT-DOSE in GY on its own grid, which SimpleITK reads as a
        /// volume. (Core.Tests' SyntheticRtDose sits in a test file of its own, so it is not linked here.)
        /// </summary>
        private static string WriteDose(string dir, SyntheticPatient p)
        {
            const int columns = 6, rows = 5, frames = 3;
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
                { DicomTag.SOPInstanceUID, DicomTestData.NewUid() },
                { DicomTag.PatientID, p.PatientId },
                { DicomTag.StudyInstanceUID, p.StudyUid },
                { DicomTag.SeriesInstanceUID, p.DoseSeriesUid },
                { DicomTag.Modality, "RTDOSE" },
                { DicomTag.FrameOfReferenceUID, p.FrameUid },
                { DicomTag.SeriesDescription, "Plan dose" },
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
                stored[i] = (ushort)(i * 1009 + 17);
            byte[] bytes = new byte[stored.Length * 2];
            Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "RD.dcm");
            new DicomFile(ds).Save(path);
            return path;
        }

        /// <summary>
        /// Adds the identifiers the test helpers do not write (name, dates, accession, physician,
        /// institution) plus a private element holding the name and ID, and records the SOP
        /// Instance UID. Values are written the way a planning system writes them.
        /// </summary>
        private static void StampIdentifiers(string path, SyntheticPatient p)
        {
            var file = DicomFile.Open(path);
            var ds = file.Dataset;
            ds.AddOrUpdate(DicomTag.PatientName, p.PatientName);
            ds.AddOrUpdate(DicomTag.PatientBirthDate, p.BirthDate);
            ds.AddOrUpdate(DicomTag.PatientSex, "O");
            ds.AddOrUpdate(DicomTag.StudyDate, p.StudyDate);
            ds.AddOrUpdate(DicomTag.SeriesDate, p.StudyDate);
            ds.AddOrUpdate(DicomTag.ContentDate, p.StudyDate);
            ds.AddOrUpdate(DicomTag.StudyTime, "101112");
            ds.AddOrUpdate(DicomTag.AccessionNumber, p.AccessionNumber);
            ds.AddOrUpdate(DicomTag.ReferringPhysicianName, p.ReferringPhysician);
            ds.AddOrUpdate(DicomTag.InstitutionName, p.InstitutionName);
            ds.AddOrUpdate(new DicomLongString(new DicomTag(0x0029, 0x0010), "SYNTHETIC PHI AUDIT"));
            ds.AddOrUpdate(new DicomLongString(new DicomTag(0x0029, 0x1010), p.PatientName + " " + p.PatientId));
            p.SopInstanceUids.Add(ds.GetString(DicomTag.SOPInstanceUID));
            file.Save(path);
        }

        // ── the audit ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Searches every file under <paramref name="root"/> except <paramref name="keyPath"/>:
        /// its path, its raw bytes, and its decompressed bytes when it is gzip.
        /// </summary>
        private static void AuditTree(
            string root, string keyPath, List<KeyValuePair<string, string>> identifiers, List<string> leaks)
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(keyPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                AuditText(rel + " (path)", rel, identifiers, leaks);

                byte[] bytes = File.ReadAllBytes(file);
                AuditBytes(rel, bytes, identifiers, leaks);
                if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
                    AuditBytes(rel + " (decompressed)", Gunzip(bytes), identifiers, leaks);
            }
        }

        private static void AuditBytes(
            string label, byte[] bytes, List<KeyValuePair<string, string>> identifiers, List<string> leaks)
        {
            foreach (var id in identifiers)
            {
                foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
                {
                    if (IndexOf(bytes, encoding.GetBytes(id.Value)) >= 0)
                        leaks.Add($"{label}: {id.Key} '{id.Value}'" +
                                  (ReferenceEquals(encoding, Encoding.Unicode) ? " (UTF-16)" : ""));
                }
            }
        }

        private static void AuditText(
            string label, string text, List<KeyValuePair<string, string>> identifiers, List<string> leaks)
        {
            foreach (var id in identifiers)
            {
                if (text.IndexOf(id.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                    leaks.Add($"{label}: {id.Key} '{id.Value}'");
            }
        }

        private static byte[] Gunzip(byte[] bytes)
        {
            using (var input = new MemoryStream(bytes))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return output.ToArray();
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
                return -1;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0])
                    continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }
    }
}
