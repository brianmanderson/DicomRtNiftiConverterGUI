using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using itk.simple;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Builds DICOM RT-DOSE (.dcm) files from NIfTI dose volumes. Each .nii.gz file in the
    /// <c>&lt;dicomFolder&gt;/doses/</c> subdirectory becomes its own RT-DOSE; the file's
    /// basename is incorporated into the output filename. Doses are written at their native
    /// NIfTI grid (no resampling); spatial alignment to the reference image series is via
    /// FrameOfReferenceUID.
    /// </summary>
    public class RtDoseWriterService
    {
        // SOP Class UID for RT Dose Storage
        private const string RtDoseSopClassUid = "1.2.840.10008.5.1.4.1.1.481.2";

        /// <summary>
        /// Discovers <paramref name="dicomFolder"/>/doses/*.nii.gz, builds one RT-DOSE per file
        /// referencing <paramref name="referenceSeries"/>, writes each into <paramref name="dicomFolder"/>.
        /// Returns the list of written file paths. When <paramref name="referenceSeries"/> is
        /// null, falls back to a metadata-driven shell built from <paramref name="metadata"/>.
        /// </summary>
        public List<string> ConvertDoseFolderToRtDoses(
            string dicomFolder,
            DicomSeriesGroup referenceSeries,
            IProgress<string> progress,
            CancellationToken ct,
            bool useStableHashNames = false,
            bool skipIfExists = false,
            NiftiPatientMetadata metadata = null)
        {
            ct.ThrowIfCancellationRequested();

            var written = new List<string>();

            if (string.IsNullOrEmpty(dicomFolder) || !Directory.Exists(dicomFolder))
                throw new InvalidOperationException("DICOM folder does not exist.");

            string dosesDir = Path.Combine(dicomFolder, "doses");
            if (!Directory.Exists(dosesDir))
                return written;

            var dupeWarnings = new List<string>();
            var doseFiles = NiftiFileNaming.EnumerateNiftiFiles(dosesDir, dupeWarnings)
                                           .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                           .ToList();
            foreach (var dup in dupeWarnings)
                progress?.Report($"  Skipping {Path.GetFileName(dup)} — gzipped variant present.");
            if (doseFiles.Count == 0)
                return written;

            bool hasReferenceSeries = referenceSeries != null
                && referenceSeries.FilePaths != null
                && referenceSeries.FilePaths.Count > 0;

            if (!hasReferenceSeries && metadata == null)
                throw new InvalidOperationException(
                    "Reference image series has no DICOM files and no metadata was provided.");

            DicomDataset refDs;
            if (hasReferenceSeries)
            {
                // Open the first reference DICOM file once to copy patient/study metadata.
                var sortedDicomFiles = SortFilesBySlicePosition(referenceSeries.FilePaths);
                var refDicom = DicomFile.Open(sortedDicomFiles[0], FileReadOption.SkipLargeTags);
                refDs = refDicom.Dataset;
            }
            else
            {
                progress?.Report("No reference DICOM series — using metadata.json for patient/study tags.");
                refDs = new NiftiMetadataService().BuildSyntheticRefDataset(metadata);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            foreach (var dosePath in doseFiles)
            {
                ct.ThrowIfCancellationRequested();

                string baseName = NiftiFileNaming.StripNiftiExtension(dosePath);

                // Compute the output path up-front so we can skip if it already exists.
                string outName = useStableHashNames
                    ? HashNaming.RtDoseFileName(baseName)
                    : $"RTDOSE_{SanitizeFileName(baseName)}_{timestamp}.dcm";
                string outPath = Path.Combine(dicomFolder, outName);

                if (skipIfExists && File.Exists(outPath))
                {
                    progress?.Report($"  Skip '{baseName}' (existing {outName}).");
                    written.Add(outPath);
                    continue;
                }

                progress?.Report($"Processing dose '{baseName}'...");

                Image dose;
                try
                {
                    dose = SimpleITK.ReadImage(dosePath);
                }
                catch (Exception ex)
                {
                    progress?.Report($"  Failed to read {Path.GetFileName(dosePath)}: {ex.Message}");
                    continue;
                }

                // Cast to float32 for max-finding and scaling.
                Image doseF = (dose.GetPixelID() == PixelIDValueEnum.sitkFloat32)
                    ? dose
                    : SimpleITK.Cast(dose, PixelIDValueEnum.sitkFloat32);
                if (!ReferenceEquals(doseF, dose))
                    dose.Dispose();

                BuildPixelDataAndScaling(
                    doseF,
                    out double scaling,
                    out int rows,
                    out int cols,
                    out int depth,
                    out double[] origin,
                    out double[] spacing,
                    out double[] direction,
                    out byte[] pixelBytes);
                doseF.Dispose();

                // Build the RT-DOSE dataset.
                var rtDs = BuildRtDoseShell(refDs, referenceSeries, baseName, timestamp);

                // Image plane / pixel module
                rtDs.AddOrUpdate(DicomTag.Rows, (ushort)rows);
                rtDs.AddOrUpdate(DicomTag.Columns, (ushort)cols);
                rtDs.AddOrUpdate(DicomTag.NumberOfFrames, depth.ToString());
                rtDs.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
                rtDs.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
                rtDs.AddOrUpdate(DicomTag.BitsAllocated, (ushort)32);
                rtDs.AddOrUpdate(DicomTag.BitsStored, (ushort)32);
                rtDs.AddOrUpdate(DicomTag.HighBit, (ushort)31);
                rtDs.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);

                rtDs.AddOrUpdate(DicomTag.PixelSpacing, new[]
                {
                    FormatDicomDS(spacing[1]),
                    FormatDicomDS(spacing[0])
                });
                rtDs.AddOrUpdate(DicomTag.SliceThickness, FormatDicomDS(spacing[2]));
                rtDs.AddOrUpdate(DicomTag.ImagePositionPatient, new[]
                {
                    FormatDicomDS(origin[0]),
                    FormatDicomDS(origin[1]),
                    FormatDicomDS(origin[2])
                });

                // Direction matrix is 3x3 row-major. ImageOrientationPatient is the first two
                // columns (x and y axis vectors) flattened: [Xx, Xy, Xz, Yx, Yy, Yz].
                rtDs.AddOrUpdate(DicomTag.ImageOrientationPatient, new[]
                {
                    FormatDicomDS(direction[0]),
                    FormatDicomDS(direction[3]),
                    FormatDicomDS(direction[6]),
                    FormatDicomDS(direction[1]),
                    FormatDicomDS(direction[4]),
                    FormatDicomDS(direction[7])
                });

                // GridFrameOffsetVector: z-offset of each frame relative to ImagePositionPatient.
                var offsets = new string[depth];
                for (int z = 0; z < depth; z++)
                    offsets[z] = FormatDicomDS(z * spacing[2]);
                rtDs.AddOrUpdate(DicomTag.GridFrameOffsetVector, offsets);

                // FrameIncrementPointer points to GridFrameOffsetVector tag.
                rtDs.AddOrUpdate(DicomTag.FrameIncrementPointer, DicomTag.GridFrameOffsetVector);

                // RT-DOSE specific tags
                rtDs.AddOrUpdate(DicomTag.DoseUnits, "GY");
                rtDs.AddOrUpdate(DicomTag.DoseType, "PHYSICAL");
                rtDs.AddOrUpdate(DicomTag.DoseSummationType, "PLAN");
                rtDs.AddOrUpdate(DicomTag.DoseGridScaling, FormatDicomDS(scaling));

                // Pixel data — 32-bit unsigned, little-endian, encoded as OW.
                // fo-dicom requires an IByteBuffer for raw byte arrays; MemoryByteBuffer wraps it.
                rtDs.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixelBytes)));

                // Save (outPath/outName were computed at the top of the loop iteration).
                var outFile = new DicomFile(rtDs);
                outFile.Save(outPath);

                progress?.Report($"  Wrote RT-DOSE: {outName}");
                written.Add(outPath);
            }

            return written;
        }

        // ---------- Internals ----------

        /// <summary>
        /// Reads dose voxel values, finds the max, computes a uint32 scaling factor, and returns
        /// the scaled little-endian uint32 byte buffer suitable for the DICOM PixelData element.
        /// </summary>
        private static void BuildPixelDataAndScaling(
            Image dose,
            out double scaling,
            out int rows,
            out int cols,
            out int depth,
            out double[] origin,
            out double[] spacing,
            out double[] direction,
            out byte[] pixelBytes)
        {
            var size = dose.GetSize();
            cols = (int)size[0];
            rows = (int)size[1];
            depth = (int)size[2];

            var sp = dose.GetSpacing();
            spacing = new[] { sp[0], sp[1], sp[2] };
            var og = dose.GetOrigin();
            origin = new[] { og[0], og[1], og[2] };
            var dir = dose.GetDirection();
            direction = new double[dir.Count];
            for (int i = 0; i < dir.Count; i++) direction[i] = dir[i];

            long total = (long)cols * rows * depth;
            if (total > int.MaxValue / 4)
                throw new InvalidOperationException(
                    $"Dose volume too large for 32-bit pixel encoding ({total} voxels).");
            int totalInt = (int)total;

            // Read the float buffer directly into managed memory and do the scaling in C#.
            // We avoid SimpleITK.Divide because its denominator-zero safety check trips on the
            // very small scaling factors that result from typical dose ranges
            // (scaling = maxDose / uint.MaxValue is ~1e-8 for a max dose of ~80 Gy).
            IntPtr floatBuf = dose.GetBufferAsFloat();
            float[] floatData = new float[totalInt];
            Marshal.Copy(floatBuf, floatData, 0, totalInt);

            // Find the max (clipping NaN/infinity and treating negatives as 0).
            double maxVal = 0;
            for (int i = 0; i < totalInt; i++)
            {
                float v = floatData[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                if (v > maxVal) maxVal = v;
            }

            pixelBytes = new byte[(long)totalInt * 4];

            if (maxVal <= 0)
            {
                // All-zero (or invalid) volume — write zeros with neutral scaling.
                scaling = 1.0;
                return;
            }

            scaling = maxVal / uint.MaxValue;
            double inv = 1.0 / scaling; // multiply is faster than divide in the inner loop

            for (int i = 0; i < totalInt; i++)
            {
                double v = floatData[i];
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) v = 0;
                double scaled = v * inv;
                if (scaled > uint.MaxValue) scaled = uint.MaxValue;
                uint u = (uint)Math.Round(scaled);
                int o = i * 4;
                pixelBytes[o]     = (byte)(u & 0xFF);
                pixelBytes[o + 1] = (byte)((u >> 8) & 0xFF);
                pixelBytes[o + 2] = (byte)((u >> 16) & 0xFF);
                pixelBytes[o + 3] = (byte)((u >> 24) & 0xFF);
            }
        }

        /// <summary>
        /// Builds the RT-DOSE-level metadata: patient + study copied from refDs, fresh series + SOP,
        /// FrameOfReferenceUID, and modality fields. Pixel-module fields are added by the caller.
        /// </summary>
        private static DicomDataset BuildRtDoseShell(
            DicomDataset refDs,
            DicomSeriesGroup referenceSeries,
            string baseName,
            string timestamp)
        {
            var ds = new DicomDataset();

            string seriesInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string sopInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string nowDate = DateTime.Now.ToString("yyyyMMdd");
            string nowTime = DateTime.Now.ToString("HHmmss");

            // SOP common (MediaStorageSOPClassUID/SOPInstanceUID are Group 0002
            // File Meta Information tags — fo-dicom populates them automatically
            // when DicomFile is constructed from the dataset).
            ds.AddOrUpdate(DicomTag.SOPClassUID, RtDoseSopClassUid);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
            ds.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");

            // Patient
            CopyTagIfPresent(refDs, ds, DicomTag.PatientID);
            CopyTagIfPresent(refDs, ds, DicomTag.PatientName);
            CopyTagIfPresent(refDs, ds, DicomTag.PatientBirthDate);
            CopyTagIfPresent(refDs, ds, DicomTag.PatientSex);

            // Study (copied)
            CopyTagIfPresent(refDs, ds, DicomTag.StudyInstanceUID);
            CopyTagIfPresent(refDs, ds, DicomTag.StudyDate);
            CopyTagIfPresent(refDs, ds, DicomTag.StudyTime);
            CopyTagIfPresent(refDs, ds, DicomTag.AccessionNumber);
            CopyTagIfPresent(refDs, ds, DicomTag.ReferringPhysicianName);
            CopyTagIfPresent(refDs, ds, DicomTag.StudyID);

            // RT-DOSE series (new)
            ds.AddOrUpdate(DicomTag.Modality, "RTDOSE");
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesInstanceUid);
            ds.AddOrUpdate(DicomTag.SeriesNumber, 1);
            ds.AddOrUpdate(DicomTag.SeriesDescription, $"Generated RTDOSE: {baseName}");
            ds.AddOrUpdate(DicomTag.InstanceNumber, 1);

            // Manufacturer / instance creation
            ds.AddOrUpdate(DicomTag.Manufacturer, "Dicom_RT_images_Csharp");
            ds.AddOrUpdate(DicomTag.InstanceCreationDate, nowDate);
            ds.AddOrUpdate(DicomTag.InstanceCreationTime, nowTime);
            ds.AddOrUpdate(DicomTag.ContentDate, nowDate);
            ds.AddOrUpdate(DicomTag.ContentTime, nowTime);

            // Frame of reference (copy from CT, fall back to series field)
            string frameUid = GetStringTag(refDs, DicomTag.FrameOfReferenceUID,
                referenceSeries?.FrameOfReferenceUID ?? "");
            if (string.IsNullOrEmpty(frameUid))
                frameUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, frameUid);
            ds.AddOrUpdate(DicomTag.PositionReferenceIndicator, "");

            ds.AddOrUpdate(DicomTag.ApprovalStatus, "UNAPPROVED");

            return ds;
        }

        private static void CopyTagIfPresent(DicomDataset src, DicomDataset dst, DicomTag tag)
        {
            try
            {
                if (src.Contains(tag))
                {
                    string v = src.GetSingleValueOrDefault(tag, "");
                    dst.AddOrUpdate(tag, v ?? "");
                }
            }
            catch (Exception) { }
        }

        private static string GetStringTag(DicomDataset ds, DicomTag tag, string defaultValue)
        {
            try
            {
                if (ds.Contains(tag))
                    return ds.GetSingleValueOrDefault(tag, defaultValue);
            }
            catch (Exception) { }
            return defaultValue;
        }

        /// <summary>
        /// Formats a double as a DICOM DS (Decimal String) value: at most 16 characters per value.
        /// Selects the highest precision that fits within the 16-character VR limit.
        /// </summary>
        private static string FormatDicomDS(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return "0";

            for (int prec = 16; prec >= 1; prec--)
            {
                string s = d.ToString("G" + prec, System.Globalization.CultureInfo.InvariantCulture);
                if (s.Length <= 16) return s;
            }
            return ((long)Math.Round(d)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static List<string> SortFilesBySlicePosition(List<string> filePaths)
        {
            var filePositions = new List<Tuple<string, double>>();
            foreach (var path in filePaths)
            {
                double zPos = 0;
                try
                {
                    var dcm = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                    if (dcm.Dataset.Contains(DicomTag.ImagePositionPatient))
                    {
                        var positions = dcm.Dataset.GetValues<double>(DicomTag.ImagePositionPatient);
                        if (positions.Length >= 3) zPos = positions[2];
                    }
                }
                catch (Exception) { /* leave at 0 */ }
                filePositions.Add(Tuple.Create(path, zPos));
            }
            return filePositions.OrderBy(t => t.Item2).Select(t => t.Item1).ToList();
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) name = name.Replace(c, '_');
            return name;
        }
    }
}
