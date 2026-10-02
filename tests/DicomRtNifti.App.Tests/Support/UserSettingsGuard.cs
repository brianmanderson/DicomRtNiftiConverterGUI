using System;
using System.IO;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// SettingsService is hard-wired to the per-user application-data folder (%AppData%\DicomToNifti
    /// on Windows, XDG config on Linux/macOS), MainViewModel reads it on construction and saves it
    /// on every Convert. On a developer machine that would both perturb the test (a saved
    /// DefaultOutputDirectory, AutoOpenAfterConversion launching a file manager, a stored output
    /// spacing) and overwrite real preferences. Each workflow test therefore renames the two JSON
    /// files aside on disk (<c>*.gui-test-bak</c>) so the view-model starts from defaults, and
    /// renames them back on dispose. The originals never leave the disk: if the test host dies
    /// before Dispose, the next guard finds the backup and restores it before doing anything else.
    /// CI runners start empty, so there the guard is a no-op.
    /// </summary>
    public sealed class UserSettingsGuard : IDisposable
    {
        private const string BackupSuffix = ".gui-test-bak";
        private static readonly string[] Files = { "settings.json", "roi_associations.json" };

        private readonly string _folder;

        public UserSettingsGuard()
        {
            _folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DicomToNifti");
            foreach (string name in Files)
            {
                string path = Path.Combine(_folder, name);
                string backup = path + BackupSuffix;

                // A backup left behind by a run that died mid-test holds the user's real file;
                // whatever sits at the live path was written by that dead test.
                if (File.Exists(backup))
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(backup, path);
                }

                if (File.Exists(path))
                    File.Move(path, backup);
            }
        }

        public void Dispose()
        {
            foreach (string name in Files)
            {
                string path = Path.Combine(_folder, name);
                string backup = path + BackupSuffix;
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(backup)) File.Move(backup, path);
            }
        }
    }
}
