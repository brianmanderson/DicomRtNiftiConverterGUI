using System;
using System.IO;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// SettingsService is hard-wired to the per-user application-data folder (%AppData%\DicomToNifti
    /// on Windows, XDG config on Linux/macOS), MainViewModel reads it on construction and saves it
    /// on every Convert. On a developer machine that would both perturb the test (a saved
    /// DefaultOutputDirectory, AutoOpenAfterConversion launching a file manager, a stored output
    /// spacing) and overwrite real preferences. Each workflow test therefore moves the two JSON
    /// files aside so the view-model starts from defaults, and puts them back on dispose. CI
    /// runners start empty, so there the guard is a no-op.
    /// </summary>
    public sealed class UserSettingsGuard : IDisposable
    {
        private static readonly string[] Files = { "settings.json", "roi_associations.json" };

        private readonly string _folder;
        private readonly byte[][] _snapshot = new byte[Files.Length][];

        public UserSettingsGuard()
        {
            _folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DicomToNifti");
            for (int i = 0; i < Files.Length; i++)
            {
                string path = Path.Combine(_folder, Files[i]);
                if (!File.Exists(path)) continue;
                _snapshot[i] = File.ReadAllBytes(path);
                File.Delete(path);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < Files.Length; i++)
            {
                string path = Path.Combine(_folder, Files[i]);
                if (_snapshot[i] == null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllBytes(path, _snapshot[i]);
                }
            }
        }
    }
}
