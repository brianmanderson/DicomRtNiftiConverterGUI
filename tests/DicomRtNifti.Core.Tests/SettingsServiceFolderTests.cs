using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The folder seam on <see cref="SettingsService"/> (F-13, toolkit #46): a server keeps its own
    /// state root and a test its temp folder, and neither reads or writes the desktop user's
    /// %AppData%\DicomToNifti. Nothing here touches that folder.
    /// </summary>
    public class SettingsServiceFolderTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void TheDefault_IsStillAppDataDicomToNifti()
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DicomToNifti");

            Assert.Equal(expected, SettingsService.DefaultFolder);
            Assert.Equal(expected, new SettingsService().Folder);
        }

        [Fact]
        public void AFolder_HoldsBothFiles_AndCreatesItselfOnTheFirstSave()
        {
            string folder = Path.Combine(_dir, "state", "settings");
            var service = new SettingsService(folder);

            Assert.False(Directory.Exists(folder));
            Assert.Empty(service.LoadAssociations());
            Assert.True(service.LoadSettings().ExportImages); // defaults while nothing is saved

            service.SaveSettings(new AppSettings { ExportImages = false, HashSalt = "server" });
            service.SaveAssociations(new List<RoiAssociation>
            {
                new RoiAssociation { CanonicalName = "Heart", Aliases = { "heart" } },
            });

            Assert.True(File.Exists(Path.Combine(folder, "settings.json")));
            Assert.True(File.Exists(Path.Combine(folder, "roi_associations.json")));
            var reread = new SettingsService(folder);
            Assert.False(reread.LoadSettings().ExportImages);
            Assert.Equal("server", reread.LoadSettings().HashSalt);
            Assert.Equal("Heart", Assert.Single(reread.LoadAssociations()).CanonicalName);
        }

        [Fact]
        public void TwoFolders_AreIndependent()
        {
            var one = new SettingsService(Path.Combine(_dir, "one"));
            var two = new SettingsService(Path.Combine(_dir, "two"));

            one.SaveSettings(new AppSettings { HashSalt = "one" });

            Assert.Equal("one", one.LoadSettings().HashSalt);
            Assert.Equal(new AppSettings().HashSalt, two.LoadSettings().HashSalt);
        }

        [Fact]
        public void AnUnusableFileInTheFolder_IsRefused_AsInAppData()
        {
            string folder = Path.Combine(_dir, "broken");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "settings.json"), "");

            var ex = Assert.Throws<InvalidDataException>(() => new SettingsService(folder).LoadSettings());
            Assert.Contains(Path.Combine(folder, "settings.json"), ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void NoFolder_IsRefused(string folder)
        {
            Assert.Throws<ArgumentException>(() => new SettingsService(folder));
        }
    }
}
