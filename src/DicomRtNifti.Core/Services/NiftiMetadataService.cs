using System;
using System.IO;
using System.Security.Cryptography;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;
using Newtonsoft.Json;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Loads / synthesizes / persists the patient + study + frame-of-reference metadata
    /// used by the NIfTI-only conversion path (no reference DICOM image series present).
    /// Stored as <c>&lt;dicomFolder&gt;/metadata.json</c>; missing fields are synthesized
    /// (anonymous patient, fresh UIDs) and written back so that the image / mask / dose
    /// conversion passes all reference the same StudyInstanceUID and FrameOfReferenceUID,
    /// and re-runs on the same folder are idempotent.
    /// </summary>
    public class NiftiMetadataService
    {
        private const string MetadataFileName = "metadata.json";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include
        };

        /// <summary>
        /// Reads <c>&lt;dicomFolder&gt;/metadata.json</c> if present; fills in anonymous
        /// defaults for any missing field. If anything was synthesized, the file is written
        /// back so subsequent runs reuse the same UIDs.
        /// </summary>
        public NiftiPatientMetadata LoadOrSynthesize(string dicomFolder)
        {
            if (string.IsNullOrEmpty(dicomFolder))
                throw new ArgumentException("dicomFolder must be a non-empty path.", nameof(dicomFolder));

            string path = Path.Combine(dicomFolder, MetadataFileName);
            NiftiPatientMetadata meta = null;
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    meta = JsonConvert.DeserializeObject<NiftiPatientMetadata>(json);
                }
                catch (Exception)
                {
                    // Fall through: treat unreadable / corrupt JSON as missing, regenerate.
                    meta = null;
                }
            }
            if (meta == null) meta = new NiftiPatientMetadata();

            bool changed = ApplyDefaults(meta);
            if (changed)
            {
                Save(dicomFolder, meta);
            }
            return meta;
        }

        /// <summary>
        /// Persist the metadata to disk. Used by NiftiImageWriterService after it generates
        /// the image-series SeriesInstanceUID and per-slice SOPInstanceUIDs.
        /// </summary>
        public void Save(string dicomFolder, NiftiPatientMetadata meta)
        {
            if (!Directory.Exists(dicomFolder))
                Directory.CreateDirectory(dicomFolder);
            string path = Path.Combine(dicomFolder, MetadataFileName);
            string json = JsonConvert.SerializeObject(meta, JsonSettings);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Builds a synthetic <see cref="DicomDataset"/> whose tags match the ones the
        /// existing RT-STRUCT and RT-DOSE writers copy via <c>CopyTagIfPresent</c>. Used to
        /// stand in for the first reference DICOM file when no image series is present.
        /// </summary>
        public DicomDataset BuildSyntheticRefDataset(NiftiPatientMetadata meta)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));

            var ds = new DicomDataset();
            ds.AddOrUpdate(DicomTag.PatientID, meta.PatientId ?? "");
            ds.AddOrUpdate(DicomTag.PatientName, meta.PatientName ?? "");
            ds.AddOrUpdate(DicomTag.PatientBirthDate, meta.PatientBirthDate ?? "");
            ds.AddOrUpdate(DicomTag.PatientSex, meta.PatientSex ?? "");

            ds.AddOrUpdate(DicomTag.StudyInstanceUID, meta.StudyInstanceUid ?? "");
            ds.AddOrUpdate(DicomTag.StudyDate, meta.StudyDate ?? "");
            ds.AddOrUpdate(DicomTag.StudyTime, meta.StudyTime ?? "");
            ds.AddOrUpdate(DicomTag.AccessionNumber, meta.AccessionNumber ?? "");
            ds.AddOrUpdate(DicomTag.ReferringPhysicianName, meta.ReferringPhysicianName ?? "");
            ds.AddOrUpdate(DicomTag.StudyID, meta.StudyId ?? "");

            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, meta.FrameOfReferenceUid ?? "");
            return ds;
        }

        // ----------------- internals -----------------

        /// <summary>
        /// Fills in defaults for any blank field. Returns true if any field was changed,
        /// so the caller knows to persist the file.
        /// </summary>
        private static bool ApplyDefaults(NiftiPatientMetadata meta)
        {
            bool changed = false;
            string nowDate = DateTime.Now.ToString("yyyyMMdd");
            string nowTime = DateTime.Now.ToString("HHmmss");

            if (string.IsNullOrEmpty(meta.PatientId))
            {
                meta.PatientId = "ANON_" + RandomHex(6);
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.PatientName))
            {
                meta.PatientName = "ANON^ANON";
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.StudyInstanceUid))
            {
                meta.StudyInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.StudyDate))
            {
                meta.StudyDate = nowDate;
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.StudyTime))
            {
                meta.StudyTime = nowTime;
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.FrameOfReferenceUid))
            {
                meta.FrameOfReferenceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.ImageModality))
            {
                meta.ImageModality = "CT";
                changed = true;
            }
            if (string.IsNullOrEmpty(meta.PatientPosition))
            {
                meta.PatientPosition = "HFS";
                changed = true;
            }
            // Note: ImageSeriesInstanceUid and ImageSopInstanceUids are intentionally NOT
            // synthesized here — they are filled in by NiftiImageWriterService only after a
            // successful image conversion run. If image.nii.gz is never present, they stay empty.

            return changed;
        }

        private static string RandomHex(int length)
        {
            byte[] buf = new byte[(length + 1) / 2];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            var sb = new System.Text.StringBuilder(length);
            foreach (byte b in buf) sb.AppendFormat("{0:x2}", b);
            if (sb.Length > length) sb.Length = length;
            return sb.ToString();
        }
    }
}
