using System;
using System.Collections.Generic;
using System.IO;
using Dicom_RT_images_Csharp.Models;
using Newtonsoft.Json;

namespace Dicom_RT_images_Csharp.Services
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
        /// Ensures the application data directory exists.
        /// </summary>
        private void EnsureDirectory()
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
        }

        /// <summary>
        /// Loads application settings from disk. Returns defaults if the file does not exist.
        /// </summary>
        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception)
            {
                // Return defaults on any read/parse error
            }
            return new AppSettings();
        }

        /// <summary>
        /// Saves application settings to disk.
        /// </summary>
        public void SaveSettings(AppSettings settings)
        {
            EnsureDirectory();
            string json = JsonConvert.SerializeObject(settings, JsonSettings);
            File.WriteAllText(SettingsFilePath, json);
        }

        /// <summary>
        /// Loads ROI associations from disk. Returns an empty list if the file does not exist.
        /// </summary>
        public List<RoiAssociation> LoadAssociations()
        {
            try
            {
                if (File.Exists(AssociationsFilePath))
                {
                    string json = File.ReadAllText(AssociationsFilePath);
                    return JsonConvert.DeserializeObject<List<RoiAssociation>>(json) ?? new List<RoiAssociation>();
                }
            }
            catch (Exception)
            {
                // Return empty on any read/parse error
            }
            return new List<RoiAssociation>();
        }

        /// <summary>
        /// Saves ROI associations to disk.
        /// </summary>
        public void SaveAssociations(List<RoiAssociation> associations)
        {
            EnsureDirectory();
            string json = JsonConvert.SerializeObject(associations, JsonSettings);
            File.WriteAllText(AssociationsFilePath, json);
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
            File.WriteAllText(filePath, json);
        }
    }
}
