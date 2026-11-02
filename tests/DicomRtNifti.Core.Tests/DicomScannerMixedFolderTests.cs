using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Pins what a scan finds in a folder that mixes a linked CT / RTSTRUCT / RTDOSE study with the
    /// files real exports carry alongside it: NIfTI and JSON outputs, an empty file, a short text
    /// file, a run of zero bytes, DICOM written without the Part 10 preamble (with and without file
    /// meta information), a hidden slice and a slice with no position. The expected hierarchy is
    /// what the scanner produced before reading was parallelised and the directory walk was
    /// pipelined into it; this test ran green against that version unchanged.
    /// </summary>
    public class DicomScannerMixedFolderTests
    {
        [Fact]
        public async Task Mixed_folder_scans_to_the_same_hierarchy()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string ctSeries = DicomTestData.NewUid();
                string rtSeries = DicomTestData.NewUid();
                string doseSeries = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();

                string ctDir = Path.Combine(dir, "ct");
                string rawDir = Path.Combine(ctDir, "raw");
                string rtDir = Path.Combine(dir, "rt");
                string otherDir = Path.Combine(dir, "other");
                foreach (var d in new[] { ctDir, rawDir, rtDir, otherDir })
                    Directory.CreateDirectory(d);

                var ctPaths = new List<string>();
                for (int i = 0; i < 5; i++)
                {
                    string name = $"slice_{i:00}.dcm";
                    DicomTestData.WriteImageSlice(ctDir, name, "CT", "P1", study, ctSeries, frame, i * 2.5);
                    ctPaths.Add(Path.Combine(ctDir, name));
                }

                // A slice without ImagePositionPatient is recorded, not counted.
                WriteUnpositionedSlice(Path.Combine(ctDir, "unpositioned.dcm"), study, ctSeries, frame, instanceNumber: 7);

                // Raw datasets: Part 10 minus the 128-byte preamble and "DICM" (fo-dicom reads it as
                // DICOM3NoPreamble), and minus the file meta group as well (DICOM3NoFileMetaInfo).
                // A magic-number check would reject both; the scanner must not.
                string full = Path.Combine(rawDir, "full.tmp");
                DicomTestData.WriteImageSlice(rawDir, "full.tmp", "CT", "P1", study, ctSeries, frame, 12.5);
                byte[] part10 = File.ReadAllBytes(full);
                string noPreamble = Path.Combine(rawDir, "nopreamble");
                File.WriteAllBytes(noPreamble, part10.Skip(132).ToArray());
                File.Delete(full);
                ctPaths.Add(noPreamble);

                DicomTestData.WriteImageSlice(rawDir, "full.tmp", "CT", "P1", study, ctSeries, frame, 15.0);
                part10 = File.ReadAllBytes(full);
                int metaGroupLength = BitConverter.ToInt32(part10, 132 + 8);
                string noMeta = Path.Combine(rawDir, "nometa");
                File.WriteAllBytes(noMeta, part10.Skip(132 + 12 + metaGroupLength).ToArray());
                File.Delete(full);
                ctPaths.Add(noMeta);

                // Hidden files are scanned: a dot-name on Unix, the Hidden attribute on Windows.
                DicomTestData.WriteImageSlice(ctDir, ".hidden_slice.dcm", "CT", "P1", study, ctSeries, frame, 17.5);
                string hidden = Path.Combine(ctDir, ".hidden_slice.dcm");
                if (OperatingSystem.IsWindows())
                    File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
                ctPaths.Add(hidden);

                string rtPath = DicomTestData.WriteRtStructWithContours(rtDir, "struct.dcm", study, rtSeries, frame,
                    new[]
                    {
                        new KeyValuePair<string, double[]>("GTV", DicomTestData.SquareContour(-10, 10, 5.0)),
                        new KeyValuePair<string, double[]>("Lung_L", DicomTestData.SquareContour(-30, 30, 7.5)),
                    },
                    patientId: "P1", referencedSeriesUid: ctSeries);
                DicomTestData.WriteRtDose(rtDir, "dose.dcm", "P1", study, doseSeries, frame);
                string dosePath = Path.Combine(rtDir, "dose.dcm");

                // Not DICOM, or not a dataset with study and series UIDs: skipped without an error.
                File.WriteAllBytes(Path.Combine(otherDir, "mask.nii.gz"), new byte[] { 0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8 });
                File.WriteAllText(Path.Combine(otherDir, "metadata.json"), "{\"PatientID\": \"P1\", \"Spacing\": [1.0, 1.0, 2.5]}\n");
                File.WriteAllBytes(Path.Combine(otherDir, "empty.dat"), new byte[0]);
                File.WriteAllText(Path.Combine(otherDir, "short.txt"), "hello");
                File.WriteAllBytes(Path.Combine(otherDir, "zeros.bin"), new byte[300]);

                var result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);

                Assert.Equal(16, result.TotalFileCount);
                Assert.Equal(0, result.SkippedErrorCount);
                Assert.Empty(result.SkippedErrorSamples);

                var patient = Assert.Single(result.Patients);
                Assert.Equal("P1", patient.PatientID);
                Assert.Equal("Test^Patient", patient.PatientName);
                var st = Assert.Single(patient.Studies);
                Assert.Equal(study, st.StudyInstanceUID);

                // No SeriesDate anywhere, so series order is by UID alone.
                var expectedOrder = new[] { ctSeries, rtSeries, doseSeries }.OrderBy(u => u, StringComparer.Ordinal).ToArray();
                Assert.Equal(expectedOrder, st.Series.Select(s => s.SeriesInstanceUID).ToArray());

                var ct = st.Series.Single(s => s.SeriesInstanceUID == ctSeries);
                var rt = st.Series.Single(s => s.SeriesInstanceUID == rtSeries);
                var dose = st.Series.Single(s => s.SeriesInstanceUID == doseSeries);

                Assert.Equal("CT", ct.Modality);
                Assert.Equal(frame, ct.FrameOfReferenceUID);
                Assert.Equal("CT series", ct.SeriesDescription);
                Assert.Equal(ctPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray(), ct.FilePaths.ToArray());
                Assert.Equal(new[] { 0.0, 2.5, 5.0, 7.5, 10.0, 12.5, 15.0, 17.5 }, ct.SlicePositions.ToArray());
                Assert.Equal(new[] { "unpositioned.dcm (InstanceNumber 7): ImagePositionPatient missing" }, ct.UnpositionedSlices.ToArray());
                Assert.Null(ct.PixelSpacing);
                Assert.Null(ct.SliceThickness);
                Assert.Empty(ct.RoiNames);
                Assert.Equal(RtLinkMatchRule.None, ct.LinkMatchRule);

                Assert.Equal("RTSTRUCT", rt.Modality);
                Assert.Equal(new[] { rtPath }, rt.FilePaths.ToArray());
                Assert.Empty(rt.SlicePositions);
                Assert.Equal(new[] { "GTV", "Lung_L" }, rt.RoiNames.ToArray());
                Assert.Equal(ctSeries, rt.ReferencedSeriesUID);
                Assert.Equal(frame, rt.FrameOfReferenceUID);
                Assert.Equal(RtLinkMatchRule.ReferencedSeriesUid, rt.LinkMatchRule);

                Assert.Equal("RTDOSE", dose.Modality);
                Assert.Equal(new[] { dosePath }, dose.FilePaths.ToArray());
                Assert.Equal("", dose.ReferencedSeriesUID);
                Assert.Equal(RtLinkMatchRule.FrameOfReferenceUid, dose.LinkMatchRule);

                Assert.Same(rt, ct.LinkedRtStruct);
                Assert.Same(rt, Assert.Single(ct.LinkedRtStructs));
                Assert.Same(dose, ct.LinkedRtDose);
                Assert.Same(dose, Assert.Single(ct.LinkedRtDoses));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task A_missing_root_throws_DirectoryNotFoundException()
        {
            string dir = Path.Combine(DicomTestData.NewTempDir(), "does_not_exist");
            try
            {
                await Assert.ThrowsAsync<DirectoryNotFoundException>(
                    () => new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(dir), recursive: true);
            }
        }

        /// <summary>
        /// An unreadable subdirectory ends the scan with its own exception, as it did when the whole
        /// tree was listed before reading began; it is not swallowed into a cancellation by the
        /// readers the failed walk stops.
        /// </summary>
        [Fact]
        public async Task An_unreadable_subdirectory_ends_the_scan_with_UnauthorizedAccessException()
        {
            string dir = DicomTestData.NewTempDir();
            string locked = Path.Combine(dir, "zz_locked");
            Directory.CreateDirectory(locked);
            for (int i = 0; i < 60; i++)
                DicomTestData.WriteImageSlice(dir, $"slice_{i:00}.dcm", "CT", "P1", DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(), i);
            File.WriteAllText(Path.Combine(locked, "inside.txt"), "x");

            Action unlock = Lock(locked);
            try
            {
                bool stillListable;
                try { Directory.GetFiles(locked); stillListable = true; }
                catch (UnauthorizedAccessException) { stillListable = false; }
                // Running elevated (or as root) the lock does not bite; nothing to test then.
                if (stillListable) return;

                await Assert.ThrowsAsync<UnauthorizedAccessException>(
                    () => new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None));
            }
            finally
            {
                unlock();
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task A_cancelled_token_throws_OperationCanceledException()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"slice_{i}.dcm", "CT", "P1", DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(), i);

                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => new DicomScannerService().ScanFolderAsync(dir, null, cts.Token));
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// Cancelling mid-scan stops reading: the files already being read finish, and nothing near
        /// the end of the folder is reached.
        /// </summary>
        [Fact]
        public async Task Cancelling_mid_scan_stops_reading_promptly()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid();
                string series = DicomTestData.NewUid();
                string frame = DicomTestData.NewUid();
                for (int i = 0; i < 400; i++)
                    DicomTestData.WriteImageSlice(dir, $"slice_{i:000}.dcm", "CT", "P1", study, series, frame, i);

                using (var cts = new CancellationTokenSource())
                {
                    var progress = new CancelOnFirstScanReport(cts);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => new DicomScannerService().ScanFolderAsync(dir, progress, cts.Token));
                    Assert.True(progress.Cancelled);
                    Assert.DoesNotContain(progress.Messages, m => m.StartsWith("Scanned 350", StringComparison.Ordinal));
                    Assert.DoesNotContain("Scan complete.", progress.Messages);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private sealed class CancelOnFirstScanReport : IProgress<string>
        {
            private readonly CancellationTokenSource _cts;
            private readonly List<string> _messages = new List<string>();

            public CancelOnFirstScanReport(CancellationTokenSource cts) { _cts = cts; }

            public bool Cancelled => _cts.IsCancellationRequested;

            public List<string> Messages
            {
                get { lock (_messages) return new List<string>(_messages); }
            }

            public void Report(string value)
            {
                lock (_messages) _messages.Add(value);
                if (value.StartsWith("Scanned ", StringComparison.Ordinal))
                    _cts.Cancel();
            }
        }

        private static void WriteUnpositionedSlice(string path, string studyUid, string seriesUid, string frameUid, int instanceNumber)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, "P1" },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, "CT" },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, "CT series" },
                { DicomTag.InstanceNumber, instanceNumber.ToString(CultureInfo.InvariantCulture) },
            };
            new DicomFile(ds).Save(path);
        }

        /// <summary>Makes a directory unlistable for the current user; returns the undo.</summary>
        private static Action Lock(string directory)
        {
            if (OperatingSystem.IsWindows())
                return LockOnWindows(directory);
            return LockOnUnix(directory);
        }

        [SupportedOSPlatform("windows")]
        private static Action LockOnWindows(string directory)
        {
            var info = new DirectoryInfo(directory);
            var acl = info.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
            return () =>
            {
                var restore = info.GetAccessControl();
                restore.RemoveAccessRule(rule);
                info.SetAccessControl(restore);
            };
        }

        [UnsupportedOSPlatform("windows")]
        private static Action LockOnUnix(string directory)
        {
            var mode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(directory, UnixFileMode.None);
            return () => File.SetUnixFileMode(directory, mode);
        }
    }
}
