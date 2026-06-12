using System.Collections.Generic;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Tests the one-time migration from the legacy flat <see cref="AppSettings.MetadataTagKeywords"/>
    /// to the per-section lists.
    /// </summary>
    public class MetadataSettingsMigrationTests
    {
        [Fact]
        public void Migrates_LegacyList_IntoImageList_AndClearsLegacy()
        {
            var settings = new AppSettings
            {
                MetadataTagKeywords = new List<string> { "PatientName", "SliceThickness" }
            };

            SettingsService.MigrateMetadataTagKeywords(settings);

            Assert.Equal(new[] { "PatientName", "SliceThickness" }, settings.MetadataImageTagKeywords);
            Assert.Empty(settings.MetadataTagKeywords);
            Assert.Empty(settings.MetadataStructureTagKeywords);
            Assert.Empty(settings.MetadataDoseTagKeywords);
        }

        [Fact]
        public void NoOp_WhenNewListsAlreadyPopulated()
        {
            var settings = new AppSettings
            {
                MetadataTagKeywords = new List<string> { "PatientName" },
                MetadataImageTagKeywords = new List<string> { "Modality" }
            };

            SettingsService.MigrateMetadataTagKeywords(settings);

            // New lists already populated -> legacy is left untouched, nothing copied over.
            Assert.Equal(new[] { "Modality" }, settings.MetadataImageTagKeywords);
            Assert.Equal(new[] { "PatientName" }, settings.MetadataTagKeywords);
        }

        [Fact]
        public void NoOp_WhenLegacyEmpty()
        {
            var settings = new AppSettings();

            SettingsService.MigrateMetadataTagKeywords(settings);

            Assert.Empty(settings.MetadataImageTagKeywords);
            Assert.Empty(settings.MetadataTagKeywords);
        }

        [Fact]
        public void IsIdempotent()
        {
            var settings = new AppSettings
            {
                MetadataTagKeywords = new List<string> { "PatientName" }
            };

            SettingsService.MigrateMetadataTagKeywords(settings);
            SettingsService.MigrateMetadataTagKeywords(settings); // second pass must not change anything

            Assert.Equal(new[] { "PatientName" }, settings.MetadataImageTagKeywords);
            Assert.Empty(settings.MetadataTagKeywords);
        }
    }
}
