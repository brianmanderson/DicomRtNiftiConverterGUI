using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A file the scan cannot open is counted in <see cref="DicomScanResult.SkippedErrorCount"/>,
    /// with a sample naming it, rather than vanishing from the result. fo-dicom wraps every failure
    /// to open or read a file in DicomFileException, so before the scanner unwrapped it a locked or
    /// access-denied file was skipped as silently as a file that is not DICOM.
    /// </summary>
    public class DicomScannerUnreadableFileTests
    {
        [Fact]
        public async Task An_exclusively_locked_file_is_counted_as_unreadable()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"slice_{i}.dcm", "CT", "P1", study, series, frame, i * 2.5);
                DicomTestData.WriteImageSlice(dir, "locked.dcm", "CT", "P1", study, series, frame, 7.5);
                string locked = Path.Combine(dir, "locked.dcm");
                WriteNonDicomFiles(dir);

                DicomScanResult result;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Unix locks are advisory; where this one does not stop a reader there is nothing to test.
                    if (CanOpenForRead(locked)) return;
                    result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
                }

                Assert.Equal(1, result.SkippedErrorCount);
                string sample = Assert.Single(result.SkippedErrorSamples);
                Assert.StartsWith(locked + ": IOException: ", sample);
                var ct = Assert.Single(Assert.Single(Assert.Single(result.Patients).Studies).Series);
                Assert.Equal(3, ct.FilePaths.Count);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task A_read_denied_file_is_counted_as_unreadable()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                for (int i = 0; i < 3; i++)
                    DicomTestData.WriteImageSlice(dir, $"slice_{i}.dcm", "CT", "P1", study, series, frame, i * 2.5);
                DicomTestData.WriteImageSlice(dir, "denied.dcm", "CT", "P1", study, series, frame, 7.5);
                string denied = Path.Combine(dir, "denied.dcm");
                WriteNonDicomFiles(dir);

                DicomScanResult result;
                Action restore = DenyRead(denied);
                try
                {
                    // Running elevated (or as root) the denial does not bite; nothing to test then.
                    if (CanOpenForRead(denied)) return;
                    result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
                }
                finally
                {
                    restore();
                }

                Assert.Equal(1, result.SkippedErrorCount);
                string sample = Assert.Single(result.SkippedErrorSamples);
                Assert.StartsWith(denied + ": UnauthorizedAccessException: ", sample);
                var ct = Assert.Single(Assert.Single(Assert.Single(result.Patients).Studies).Series);
                Assert.Equal(3, ct.FilePaths.Count);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>The other side of the two tests above: a file that opens but is not DICOM is no error.</summary>
        [Fact]
        public async Task Readable_files_that_are_not_DICOM_are_skipped_without_an_error()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                WriteNonDicomFiles(dir);

                var result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);

                Assert.Equal(3, result.TotalFileCount);
                Assert.Equal(0, result.SkippedErrorCount);
                Assert.Empty(result.SkippedErrorSamples);
                Assert.Empty(result.Patients);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static void WriteNonDicomFiles(string dir)
        {
            File.WriteAllBytes(Path.Combine(dir, "mask.nii.gz"), new byte[] { 0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8 });
            File.WriteAllText(Path.Combine(dir, "metadata.json"), "{\"PatientID\": \"P1\", \"Spacing\": [1.0, 1.0, 2.5]}\n");
            File.WriteAllBytes(Path.Combine(dir, "empty.dat"), new byte[0]);
        }

        private static bool CanOpenForRead(string path)
        {
            try
            {
                using (File.OpenRead(path)) return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Makes a file unreadable for the current user; returns the undo.</summary>
        private static Action DenyRead(string path)
        {
            if (OperatingSystem.IsWindows())
                return DenyReadOnWindows(path);
            return DenyReadOnUnix(path);
        }

        [SupportedOSPlatform("windows")]
        private static Action DenyReadOnWindows(string path)
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.ReadData, AccessControlType.Deny);
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
        private static Action DenyReadOnUnix(string path)
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
            return () => File.SetUnixFileMode(path, mode);
        }
    }
}
