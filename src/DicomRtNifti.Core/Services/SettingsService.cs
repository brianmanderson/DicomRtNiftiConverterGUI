using System;
using System.Collections.Generic;
using System.IO;
using DicomRtNifti.Core.Models;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Handles loading and saving application settings and ROI associations to JSON files.
    /// </summary>
    public class SettingsService
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DicomToNifti");

        private static readonly string SettingsFilePath = Path.Combine(AppDataFolder, "settings.json");
        private static readonly string AssociationsFilePath = Path.Combine(AppDataFolder, "roi_associations.json");

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        /// <summary>
        /// Loads application settings from disk. Returns defaults if the file does not exist.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// settings.json exists but cannot be read or parsed. Returning defaults instead used to
        /// look harmless, but the next <see cref="SaveSettings"/> rewrites the file from those
        /// defaults, so a single truncated write silently discarded every stored preference with
        /// no way to tell it had happened.
        /// </exception>
        public AppSettings LoadSettings()
        {
            if (!File.Exists(SettingsFilePath))
                return new AppSettings();

            var settings = ReadJson<AppSettings>(SettingsFilePath, "application settings");
            MigrateMetadataTagKeywords(settings);
            return settings;
        }

        /// <summary>
        /// One-time migration from the legacy flat <see cref="AppSettings.MetadataTagKeywords"/> to
        /// the per-section lists: if none of the three new lists are populated and the legacy list
        /// is non-empty, its contents move into <see cref="AppSettings.MetadataImageTagKeywords"/>
        /// and the legacy list is cleared. Idempotent and a no-op once migrated; the migrated shape
        /// persists on the next <see cref="SaveSettings"/>.
        /// </summary>
        internal static void MigrateMetadataTagKeywords(AppSettings settings)
        {
            if (settings == null)
                return;

            bool newListsEmpty =
                (settings.MetadataImageTagKeywords == null || settings.MetadataImageTagKeywords.Count == 0) &&
                (settings.MetadataStructureTagKeywords == null || settings.MetadataStructureTagKeywords.Count == 0) &&
                (settings.MetadataDoseTagKeywords == null || settings.MetadataDoseTagKeywords.Count == 0);

            if (newListsEmpty && settings.MetadataTagKeywords != null && settings.MetadataTagKeywords.Count > 0)
            {
                settings.MetadataImageTagKeywords = new List<string>(settings.MetadataTagKeywords);
                settings.MetadataTagKeywords = new List<string>();
            }
        }

        /// <summary>
        /// Saves application settings to disk. The write is atomic (temp file + replace) so a
        /// crash cannot leave behind the truncated settings.json that <see cref="LoadSettings"/>
        /// now refuses to load.
        /// </summary>
        public void SaveSettings(AppSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, JsonSettings);
            AtomicFileWriter.WriteAllText(SettingsFilePath, json);
        }

        /// <summary>
        /// Loads ROI associations from disk. Returns an empty list if the file does not exist.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// roi_associations.json exists but cannot be read or parsed. These are hand-curated
        /// canonical-name/alias sets that nothing else can reconstruct, so silently substituting
        /// an empty list — which the next <see cref="SaveAssociations"/> then writes over the
        /// original — destroys user data.
        /// </exception>
        public List<RoiAssociation> LoadAssociations()
        {
            if (!File.Exists(AssociationsFilePath))
                return new List<RoiAssociation>();

            return ReadJson<List<RoiAssociation>>(AssociationsFilePath, "ROI associations");
        }

        /// <summary>
        /// Saves ROI associations to disk. Atomic, for the same reason as
        /// <see cref="SaveSettings"/>.
        /// </summary>
        public void SaveAssociations(List<RoiAssociation> associations)
        {
            string json = JsonConvert.SerializeObject(associations, JsonSettings);
            AtomicFileWriter.WriteAllText(AssociationsFilePath, json);
        }

        /// <summary>
        /// Reads and deserializes one of the app's own JSON state files, turning any failure into
        /// an <see cref="InvalidDataException"/> that names the path and the remedy. An empty file
        /// and a literal JSON null both deserialize to null without throwing, and an empty file is
        /// the classic truncated-write artefact, so a null result counts as unusable too.
        ///
        /// Internal rather than private so the refusal can be unit-tested against a temp file:
        /// the public entry points are hard-wired to %AppData%, which tests must not touch.
        /// </summary>
        internal static T ReadJson<T>(string path, string what) where T : class
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(BuildUnusableFileMessage(path, what, ex.Message), ex);
            }

            T value;
            try
            {
                value = JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(BuildUnusableFileMessage(path, what, ex.Message), ex);
            }

            if (value == null)
            {
                throw new InvalidDataException(BuildUnusableFileMessage(
                    path, what, "the file is empty or contains a JSON null"));
            }

            return value;
        }

        /// <summary>
        /// The message shown when one of the app's JSON state files exists but cannot be used.
        /// Internal so the wording is testable without touching %AppData%.
        /// </summary>
        internal static string BuildUnusableFileMessage(string path, string what, string detail)
        {
            return
                $"The {what} file '{path}' exists but could not be read ({detail}). " +
                "Refusing to continue with defaults: saving over it would discard whatever it " +
                "still holds. Move the file aside (or restore a backup) and try again.";
        }

        /// <summary>
        /// Loads ROI associations from an arbitrary JSON file path (for import).
        /// </summary>
        public List<RoiAssociation> ImportAssociations(string filePath)
        {
            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<List<RoiAssociation>>(json) ?? new List<RoiAssociation>();
        }

        /// <summary>
        /// Exports ROI associations to an arbitrary JSON file path.
        /// </summary>
        public void ExportAssociations(List<RoiAssociation> associations, string filePath)
        {
            string json = JsonConvert.SerializeObject(associations, JsonSettings);
            AtomicFileWriter.WriteAllText(filePath, json);
        }
    }
}
