using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The PHI-leak audit (W2-E). A synthetic cohort carries a patient name, patient ID, birth
    /// date, study date, accession number, referring physician, institution, every UID a real
    /// export has, and a private element holding the name; its input folders are named after
    /// the patient, as clinical exports usually are. It is exported anonymized through
    /// <see cref="CohortExportService"/> with identifying tags selected for metadata.json, then
    /// written back to RTSTRUCT, RT-DOSE and a DICOM image series the way the reverse window does
    /// on an export folder. Every byte written — NIfTI headers and payloads (also inside the gzip
    /// stream), the manifest, metadata.json, every DICOM element including private ones, the
    /// export log and the cohort result the CLI serializes — is searched for every source
    /// identifier.
    ///
    /// The one file allowed to hold them is AnonymizationKey.json: it is the re-identification
    /// key, written into the export root by design, and the test asserts it does hold them so
    /// the identifiers searched for are known to be the ones the export saw.
    /// </summary>
    public class AnonymizedExportPhiAuditTests
    {
        private const string Salt = "phi-audit-salt";

        private readonly ITestOutputHelper _output;

        public AnonymizedExportPhiAuditTests(ITestOutputHelper output)
        {
            _output = output;
        }

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

        private static readonly SyntheticPatient[] Patients =
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

        // Identifying tags a user plausibly ticks in the metadata picker, next to the ones the
        // sidecar exists for. The anonymized export must keep the latter and not leak the former.
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

        [Fact]
        public async Task AnonymizedCohortExport_AndItsReverse_WriteNoSourceIdentifier()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                string input = Path.Combine(root, "in");
                string outputRoot = Path.Combine(root, "out");
                foreach (var p in Patients)
                    WritePatient(input, p);

                var scan = await new DicomScannerService().ScanFolderAsync(input, null, CancellationToken.None);
                var options = new CohortExportOptions
                {
                    InputRoot = input,
                    OutputRoot = outputRoot,
                    Anonymize = true,
                    Salt = Salt,
                    ImageKeywords = ImageKeywords,
                    StructureKeywords = StructureKeywords,
                    DoseKeywords = DoseKeywords,
                };
                var anon = new AnonymizationService(
                    Path.Combine(outputRoot, CohortExportService.AnonymizationKeyFileName), Salt);
                var plan = CohortExportService.BuildPlan(scan, options, anon);
                Assert.Equal(Patients.Length, plan.Series.Count);
                Assert.All(plan.Series, s => Assert.NotNull(s.RtStruct));
                Assert.All(plan.Series, s => Assert.Single(s.RtDoses));

                var log = new CollectingProgress();
                var result = await new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
                    .ExecuteAsync(plan, options, anon, log, CancellationToken.None);
                Assert.Equal(0, result.FailedCount);
                Assert.Equal(Patients.Length, result.SucceededCount);

                var identifiers = Patients.SelectMany(p => p.Identifiers()).ToList();
                string keyPath = Path.Combine(outputRoot, CohortExportService.AnonymizationKeyFileName);

                // The audit is only as good as the identifiers it looks for: the key, which maps
                // each source identifier to its hash, must hold the ones the export was given.
                string key = File.ReadAllText(keyPath);
                foreach (var p in Patients)
                {
                    Assert.Contains(p.PatientId, key);
                    Assert.Contains(p.StudyUid, key);
                    Assert.Contains(p.CtSeriesUid, key);
                }

                // Pass 1: the forward export as written, before any reverse run touches it.
                var leaks = new List<string>();
                AuditTree(outputRoot, keyPath, identifiers, leaks);
                AuditText("export log", string.Join("\n", log.Messages), identifiers, leaks);
                AuditText("cohort result", JsonConvert.SerializeObject(result), identifiers, leaks);
                AuditText("cohort plan (skipped/unlinked)",
                    JsonConvert.SerializeObject(new { plan.Skipped, plan.UnlinkedRtObjects }), identifiers, leaks);

                // Kept for the checks at the end: the reverse run below rewrites metadata.json.
                var forwardSidecars = result.Series.ToDictionary(
                    s => s.SeriesUid, s => File.ReadAllText(Path.Combine(outputRoot, s.Metadata)));

                // Pass 2: write each export folder back to DICOM with no reference series, the
                // GUI's reverse path on an anonymized export, then audit everything again.
                var reverseLog = new CollectingProgress();
                foreach (var series in result.Series)
                {
                    string dir = Path.Combine(outputRoot, series.OutputDir);
                    var metadata = new NiftiMetadataService().LoadOrSynthesize(dir);
                    new NiftiImageWriterService(new NiftiMetadataService())
                        .ConvertImageNiftiToDicomSeries(dir, metadata, reverseLog, CancellationToken.None);
                    new RtStructWriterService().ConvertMasksFolderToRtStruct(
                        dir, null, Path.Combine(dir, "RS_reverse.dcm"), reverseLog, CancellationToken.None, metadata);
                    var doses = new RtDoseWriterService().ConvertDoseFolderToRtDoses(
                        dir, null, reverseLog, CancellationToken.None, useStableHashNames: true, metadata: metadata);
                    Assert.Single(doses);
                }
                Assert.NotEmpty(Directory.GetFiles(outputRoot, "*.dcm", SearchOption.AllDirectories));
                AuditTree(outputRoot, keyPath, identifiers, leaks);
                AuditText("reverse log", string.Join("\n", reverseLog.Messages), identifiers, leaks);

                var files = Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(outputRoot.Length + 1).Replace('\\', '/'))
                    .OrderBy(f => f, StringComparer.Ordinal);
                _output.WriteLine("Audited files:\n  " + string.Join("\n  ", files));

                var distinct = leaks.Distinct().ToList();
                Assert.True(distinct.Count == 0,
                    $"{distinct.Count} source identifier occurrence(s) in the anonymized export:\n  " +
                    string.Join("\n  ", distinct));

                // The sidecar keeps what it is for: non-identifying values, computed values, and
                // the export's own pseudonyms in place of the source identifiers.
                foreach (var series in result.Series)
                    AssertMetadataKeepsNonIdentifyingValues(forwardSidecars[series.SeriesUid], series, plan);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The scanner the audit relies on finds an identifier wherever it sits: plain, inside a
        /// gzip stream, as UTF-16, and in a private DICOM element. Without this, a broken scanner
        /// would pass the audit by finding nothing.
        /// </summary>
        [Fact]
        public void Audit_FindsAnIdentifierInEveryEncodingItSearches()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var ids = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("PatientID", "MRN0SELFTEST"),
                };

                File.WriteAllText(Path.Combine(root, "plain.csv"), "a,MRN0SELFTEST,b");
                using (var fs = File.Create(Path.Combine(root, "hidden.nii.gz")))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                {
                    byte[] payload = Encoding.ASCII.GetBytes(new string('x', 400) + "MRN0SELFTEST");
                    gz.Write(payload, 0, payload.Length);
                }
                File.WriteAllBytes(Path.Combine(root, "wide.json"), Encoding.Unicode.GetBytes("{\"id\":\"MRN0SELFTEST\"}"));

                var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                {
                    { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
                    { DicomTag.SOPInstanceUID, DicomTestData.NewUid() },
                };
                AddPrivatePhi(ds, "MRN0SELFTEST");
                new DicomFile(ds).Save(Path.Combine(root, "private.dcm"));

                var leaks = new List<string>();
                AuditTree(root, null, ids, leaks);
                Assert.Contains(leaks, l => l.StartsWith("plain.csv"));
                Assert.Contains(leaks, l => l.StartsWith("hidden.nii.gz (decompressed)"));
                Assert.Contains(leaks, l => l.StartsWith("wide.json"));
                Assert.Contains(leaks, l => l.StartsWith("private.dcm") && l.Contains("(0029,"));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ── the sidecar rule on its own ────────────────────────────────────────────────────

        [Fact]
        public void Deidentifier_EveryListedKeywordIsADictionaryKeyword()
        {
            // A misspelt keyword would silently never match, leaving that attribute verbatim.
            var known = new HashSet<string>(
                DicomDictionary.Default.Where(e => e != null).Select(e => e.Keyword), StringComparer.Ordinal);
            var unknown = MetadataDeidentifier.IdentifyingKeywords.Where(k => !known.Contains(k)).ToList();
            Assert.True(unknown.Count == 0, "not in fo-dicom's dictionary: " + string.Join(", ", unknown));
        }

        [Fact]
        public void Deidentifier_HashesTheThreeIdentifiers_RemovesIdentifyingValues_KeepsTheRest()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                var anon = new AnonymizationService(Path.Combine(root, "key.json"), Salt);
                var deid = new MetadataDeidentifier(anon);
                string instanceUid = DicomTestData.NewUid();

                Assert.Equal(anon.GetPatientHash("MRN1"), deid.Apply(DicomTag.PatientID, "LO", "MRN1"));
                Assert.Equal(anon.GetStudyHash(instanceUid), deid.Apply(DicomTag.StudyInstanceUID, "UI", instanceUid));
                Assert.Equal(anon.GetSeriesHash(instanceUid), deid.Apply(DicomTag.SeriesInstanceUID, "UI", instanceUid));

                // Removed by VR: names, dates, times, ages; by UID: any instance UID.
                Assert.Null(deid.Apply(DicomTag.PatientName, "PN", "Doe^Jane"));
                Assert.Null(deid.Apply(DicomTag.OperatorsName, "PN", "Tech^Ann"));
                Assert.Null(deid.Apply(DicomTag.PatientBirthDate, "DA", "19600101"));
                Assert.Null(deid.Apply(DicomTag.AcquisitionDateTime, "DT", "20200101120000"));
                Assert.Null(deid.Apply(DicomTag.StudyTime, "TM", "101112"));
                Assert.Null(deid.Apply(DicomTag.PatientAge, "AS", "091Y"));
                Assert.Null(deid.Apply(DicomTag.SOPInstanceUID, "UI", instanceUid));
                Assert.Null(deid.Apply(DicomTag.FrameOfReferenceUID, "UI", instanceUid));
                Assert.Null(deid.Apply(DicomTag.ReferencedSOPInstanceUID, "UI",
                    new[] { DicomUID.CTImageStorage.UID, instanceUid }));
                // Removed by keyword.
                Assert.Null(deid.Apply(DicomTag.AccessionNumber, "SH", "ACC1"));
                Assert.Null(deid.Apply(DicomTag.InstitutionName, "LO", "Hospital"));
                Assert.Null(deid.Apply(DicomTag.DeviceSerialNumber, "LO", "SN-1"));
                Assert.Null(deid.Apply(DicomTag.PatientComments, "LT", "seen by Dr Who"));

                // Kept: standard UIDs and non-identifying values, numbers and arrays included.
                Assert.Equal(DicomUID.RTDoseStorage.UID,
                    deid.Apply(DicomTag.SOPClassUID, "UI", DicomUID.RTDoseStorage.UID));
                Assert.Equal("O", deid.Apply(DicomTag.PatientSex, "CS", "O"));
                Assert.Equal("CT", deid.Apply(DicomTag.Modality, "CS", "CT"));
                Assert.Equal("GY", deid.Apply(DicomTag.DoseUnits, "CS", "GY"));
                Assert.Equal(1.25, deid.Apply(DicomTag.SliceThickness, "DS", 1.25));
                var spacing = new[] { 0.9, 0.9 };
                Assert.Same(spacing, deid.Apply(DicomTag.PixelSpacing, "DS", spacing));
                Assert.Null(deid.Apply(DicomTag.PatientName, "PN", null));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ── cohort synthesis ───────────────────────────────────────────────────────────────

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

            var grid = new DoseGrid
            {
                Columns = 6, Rows = 5, Frames = 3,
                Origin = new[] { 1.5, 2.5, 0.0 },
                ColumnSpacing = 2.0, RowSpacing = 2.5, FrameSpacing = 1.5,
            };
            string dose = SyntheticRtDose.Write(
                Path.Combine(patientDir, "RTDOSE"), "RD.dcm", grid, SyntheticRtDose.FullRangeValues(grid, 16), 16,
                0.001, "GY", p.PatientId, p.StudyUid, p.DoseSeriesUid, p.FrameUid, seriesDescription: "Plan dose");
            StampIdentifiers(dose, p);
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
            AddPrivatePhi(ds, p.PatientName + " " + p.PatientId);
            p.SopInstanceUids.Add(ds.GetString(DicomTag.SOPInstanceUID));
            file.Save(path);
        }

        private static void AddPrivatePhi(DicomDataset ds, string value)
        {
            ds.AddOrUpdate(new DicomLongString(new DicomTag(0x0029, 0x0010), "SYNTHETIC PHI AUDIT"));
            ds.AddOrUpdate(new DicomLongString(new DicomTag(0x0029, 0x1010), value));
        }

        // ── the audit ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Searches every file under <paramref name="root"/> except <paramref name="keyPath"/>:
        /// its raw bytes, its decompressed bytes when it is gzip, and — for DICOM — every element
        /// value including private elements and sequence items, so a hit names the tag it is in.
        /// </summary>
        private static void AuditTree(
            string root, string keyPath, List<KeyValuePair<string, string>> identifiers, List<string> leaks)
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (keyPath != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keyPath),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                AuditText(rel + " (path)", rel, identifiers, leaks);

                byte[] bytes = File.ReadAllBytes(file);
                AuditBytes(rel, bytes, identifiers, leaks);

                if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
                    AuditBytes(rel + " (decompressed)", Gunzip(bytes), identifiers, leaks);

                if (file.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase))
                    AuditDicomElements(rel, file, identifiers, leaks);
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

        private static void AuditDicomElements(
            string rel, string file, List<KeyValuePair<string, string>> identifiers, List<string> leaks)
        {
            var dicom = DicomFile.Open(file, FileReadOption.ReadAll);
            foreach (var (tag, value) in StringValues(dicom.FileMetaInfo).Concat(StringValues(dicom.Dataset)))
            {
                foreach (var id in identifiers)
                {
                    if (value.IndexOf(id.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                        leaks.Add($"{rel}: {id.Key} '{id.Value}' in {tag}{(tag.IsPrivate ? " (private)" : "")}");
                }
            }
        }

        /// <summary>Every element's value as text, recursing into sequence items.</summary>
        private static IEnumerable<(DicomTag, string)> StringValues(DicomDataset ds)
        {
            foreach (var item in ds)
            {
                if (item is DicomSequence seq)
                {
                    foreach (var child in seq.Items)
                        foreach (var v in StringValues(child))
                            yield return v;
                }
                else if (item is DicomElement el && el.Tag != DicomTag.PixelData)
                {
                    yield return (el.Tag, Encoding.UTF8.GetString(el.Buffer.Data));
                }
            }
        }

        private static void AssertMetadataKeepsNonIdentifyingValues(
            string sidecarJson, SeriesExportResult series, CohortExportPlan plan)
        {
            var planned = plan.Series.Single(s => s.ExportSeriesUid == series.SeriesUid);
            var json = JObject.Parse(sidecarJson);

            var image = (JObject)json["ImageAttributes"];
            Assert.Equal("CT", (string)image[MetadataTagCatalog.FriendlyName("Modality")]);
            Assert.Equal("CT series", (string)image[MetadataTagCatalog.FriendlyName("SeriesDescription")]);
            Assert.Equal(DicomUID.CTImageStorage.UID, (string)image[MetadataTagCatalog.FriendlyName("SOPClassUID")]);
            Assert.Equal("O", (string)image[MetadataTagCatalog.FriendlyName("PatientSex")]);
            Assert.Equal(JTokenType.Array, image[MetadataTagCatalog.FriendlyName(MetadataTagCatalog.VoxelSizeKey)].Type);
            // The pseudonyms the folder names and manifest use, so the sidecar still joins to them.
            Assert.Equal(planned.ExportPatientId, (string)image[MetadataTagCatalog.FriendlyName("PatientID")]);
            Assert.Equal(planned.ExportStudyUid, (string)image[MetadataTagCatalog.FriendlyName("StudyInstanceUID")]);
            Assert.Equal(planned.ExportSeriesUid, (string)image[MetadataTagCatalog.FriendlyName("SeriesInstanceUID")]);
            // Identifying values are written as null, keeping the key set every folder shares.
            Assert.Equal(JTokenType.Null, image[MetadataTagCatalog.FriendlyName("PatientName")].Type);
            Assert.Equal(JTokenType.Null, image[MetadataTagCatalog.FriendlyName("StudyDate")].Type);

            var structure = (JObject)json["StructureAttributes"];
            Assert.Equal("TEST", (string)structure[MetadataTagCatalog.FriendlyName("StructureSetLabel")]);
            Assert.Equal(2L, (long)structure[MetadataTagCatalog.FriendlyName(MetadataTagCatalog.RoiCountKey)]);

            var dose = (JObject)json["DoseAttributes"];
            Assert.Equal("GY", (string)dose[MetadataTagCatalog.FriendlyName("DoseUnits")]);
            Assert.Equal(65.535, (double)dose[MetadataTagCatalog.FriendlyName(MetadataTagCatalog.MaxDoseKey)], 6);
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

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
