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
    /// Writes <c>&lt;dicomFolder&gt;/image.nii.gz</c> (or <c>image.nii</c>) as a single-series,
    /// multi-slice DICOM image set (CT/MR/PT). One DICOM file per Nifti z-slice, all sharing
    /// the patient/study metadata and FrameOfReferenceUID from <see cref="NiftiPatientMetadata"/>.
    /// After a successful run, the generated SeriesInstanceUID and per-slice SOPInstanceUIDs are
    /// recorded back into the metadata so downstream RT-STRUCT/RT-DOSE writers reference them.
    /// </summary>
    public class NiftiImageWriterService
    {
        // SOP Class UIDs for the supported modalities.
        private const string CtImageStorage = "1.2.840.10008.5.1.4.1.1.2";
        private const string MrImageStorage = "1.2.840.10008.5.1.4.1.1.4";
        private const string PtImageStorage = "1.2.840.10008.5.1.4.1.1.128";

        private readonly NiftiMetadataService _metadataService;

        public NiftiImageWriterService(NiftiMetadataService metadataService)
        {
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        }

        /// <summary>
        /// If <paramref name="dicomFolder"/>/image.nii.gz (or image.nii) exists and the folder
        /// does not already contain a DICOM with the SeriesInstanceUID recorded in
        /// <paramref name="metadata"/>, converts the Nifti volume to one DICOM per slice.
        /// Returns the list of written paths (empty if no image was present or conversion was
        /// skipped).
        /// </summary>
        public List<string> ConvertImageNiftiToDicomSeries(
            string dicomFolder,
            NiftiPatientMetadata metadata,
            IProgress<string> progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(dicomFolder) || !Directory.Exists(dicomFolder))
                throw new InvalidOperationException("DICOM folder does not exist.");
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var written = new List<string>();
            if (!NiftiFileNaming.TryGetImageNiftiPath(dicomFolder, out string imagePath))
            {
                return written;
            }
            string imageFileName = Path.GetFileName(imagePath);

            // Idempotency: if the folder already contains a DICOM whose SeriesInstanceUID
            // matches the one recorded in metadata, skip regeneration.
            if (!string.IsNullOrEmpty(metadata.ImageSeriesInstanceUid)
                && FolderContainsSeries(dicomFolder, metadata.ImageSeriesInstanceUid))
            {
                progress?.Report($"  {imageFileName} already converted (series {ShortUid(metadata.ImageSeriesInstanceUid)} present) — skipping.");
                return written;
            }

            progress?.Report($"Reading {imageFileName}...");
            Image niftiImage;
            try
            {
                niftiImage = SimpleITK.ReadImage(imagePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read {imagePath}: {ex.Message}", ex);
            }

            try
            {
                return WriteImageSeries(dicomFolder, niftiImage, metadata, progress, ct, written);
            }
            finally
            {
                niftiImage.Dispose();
            }
        }

        // -------------------- internals --------------------

        private List<string> WriteImageSeries(
            string dicomFolder,
            Image niftiImage,
            NiftiPatientMetadata metadata,
            IProgress<string> progress,
            CancellationToken ct,
            List<string> written)
        {
            var size = niftiImage.GetSize();
            if (size.Count < 3)
                throw new InvalidOperationException(
                    $"Image NIfTI must be a 3D volume (got {size.Count} dimensions).");
            int cols = (int)size[0];
            int rows = (int)size[1];
            int depth = (int)size[2];

            var sp = niftiImage.GetSpacing();
            double spX = sp[0], spY = sp[1], spZ = sp[2];

            var og = niftiImage.GetOrigin();
            double oX = og[0], oY = og[1], oZ = og[2];

            var dir = niftiImage.GetDirection();
            double[] direction = new double[9];
            for (int i = 0; i < 9 && i < dir.Count; i++) direction[i] = dir[i];

            // Read voxels as float32, then cast per-slice into int16 (CT/MR) or uint16 (PT)
            // applying the inverse of RescaleSlope/RescaleIntercept so the stored pixel
            // value reproduces the input value via (stored * slope) + intercept.
            Image f32 = (niftiImage.GetPixelID() == PixelIDValueEnum.sitkFloat32)
                ? niftiImage
                : SimpleITK.Cast(niftiImage, PixelIDValueEnum.sitkFloat32);
            try
            {
                long total = (long)cols * rows * depth;
                if (total > int.MaxValue)
                    throw new InvalidOperationException($"Image too large ({total} voxels).");
                int totalInt = (int)total;
                float[] floatData = new float[totalInt];
                IntPtr buf = f32.GetBufferAsFloat();
                Marshal.Copy(buf, floatData, 0, totalInt);

                string modality = string.IsNullOrEmpty(metadata.ImageModality) ? "CT" : metadata.ImageModality;
                bool signedPixels = !string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase);
                string sopClassUid = SopClassUidFor(modality);

                // Adaptive RescaleSlope for PT: PET SUV / activity-concentration values
                // are continuous and typically span a small dynamic range (e.g. [0, 20]).
                // Encoding them with slope=1 into uint16 collapses sub-integer detail —
                // a 0.4 SUV reconstruction error is then baked in by rounding. If the
                // caller has not provided an explicit slope, derive one from the actual
                // data range so each LSB represents ~max/65535 SUV, bounding the
                // quantization error by half an LSB per voxel.
                if (string.Equals(modality, "PT", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(metadata.ImageRescaleSlope - 1.0) < 1e-12
                    && Math.Abs(metadata.ImageRescaleIntercept) < 1e-12)
                {
                    double maxVal = 0.0;
                    for (int i = 0; i < totalInt; i++)
                    {
                        double v = floatData[i];
                        if (v > maxVal) maxVal = v;
                    }
                    double headroom = signedPixels
                        ? (double)short.MaxValue
                        : (double)ushort.MaxValue;
                    if (maxVal > 0 && headroom > 0)
                    {
                        metadata.ImageRescaleSlope = maxVal / headroom;
                        progress?.Report(
                            $"  PT adaptive RescaleSlope = {metadata.ImageRescaleSlope:G6} "
                            + $"(max={maxVal:G6} / headroom={headroom}).");
                    }
                }

                // Fresh series UID + per-slice SOP UIDs. Persist into metadata at end.
                string seriesInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                var perSliceSopUids = new List<string>(depth);
                string nowDate = DateTime.Now.ToString("yyyyMMdd");
                string nowTime = DateTime.Now.ToString("HHmmss");

                // ImageOrientationPatient: first two columns of the 3×3 direction matrix
                // (Xx, Xy, Xz, Yx, Yy, Yz). Matches the convention used by RtDoseWriterService.
                string[] iop = new[]
                {
                    FormatDs(direction[0]), FormatDs(direction[3]), FormatDs(direction[6]),
                    FormatDs(direction[1]), FormatDs(direction[4]), FormatDs(direction[7])
                };

                // The third column of the direction matrix is the slice-step unit vector.
                double dzX = direction[2], dzY = direction[5], dzZ = direction[8];

                int sliceLen = cols * rows;
                for (int z = 0; z < depth; z++)
                {
                    ct.ThrowIfCancellationRequested();

                    string sopInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                    perSliceSopUids.Add(sopInstanceUid);

                    // ImagePositionPatient = origin + z * spacing[2] * direction_z
                    double ippX = oX + z * spZ * dzX;
                    double ippY = oY + z * spZ * dzY;
                    double ippZ = oZ + z * spZ * dzZ;

                    byte[] pixelBytes = EncodeSlice(
                        floatData, z, sliceLen,
                        metadata.ImageRescaleSlope, metadata.ImageRescaleIntercept,
                        signedPixels);

                    // SliceLocation is the projection of ImagePositionPatient onto the slice
                    // normal (third column of the direction matrix). For axial-aligned scans
                    // this is identical to ippZ; for tilted scans it is the dot product. TPS
                    // systems (Eclipse) often refuse import without SliceLocation present.
                    double sliceLocation = ippX * dzX + ippY * dzY + ippZ * dzZ;

                    var ds = BuildImageSliceDataset(
                        metadata: metadata,
                        sopClassUid: sopClassUid,
                        sopInstanceUid: sopInstanceUid,
                        seriesInstanceUid: seriesInstanceUid,
                        modality: modality,
                        nowDate: nowDate,
                        nowTime: nowTime,
                        rows: rows,
                        cols: cols,
                        spX: spX,
                        spY: spY,
                        spZ: spZ,
                        ippX: ippX,
                        ippY: ippY,
                        ippZ: ippZ,
                        iop: iop,
                        instanceNumber: z + 1,
                        signedPixels: signedPixels,
                        pixelBytes: pixelBytes,
                        sliceLocation: sliceLocation);

                    string outName = $"image_{(z + 1):D4}.dcm";
                    string outPath = Path.Combine(dicomFolder, outName);
                    var outFile = new DicomFile(ds);
                    outFile.Save(outPath);
                    written.Add(outPath);

                    if ((z + 1) % 25 == 0 || z + 1 == depth)
                        progress?.Report($"  image slice {z + 1}/{depth} written.");
                }

                // Persist the generated UIDs so re-runs and downstream writers reference them.
                metadata.ImageSeriesInstanceUid = seriesInstanceUid;
                metadata.ImageSopInstanceUids = perSliceSopUids;
                _metadataService.Save(dicomFolder, metadata);

                progress?.Report($"  Wrote image series: {written.Count} slice(s), series {ShortUid(seriesInstanceUid)}.");
                return written;
            }
            finally
            {
                if (!ReferenceEquals(f32, niftiImage)) f32.Dispose();
            }
        }

        /// <summary>
        /// Encodes a single z-slice's voxel values into a little-endian 16-bit byte buffer.
        /// Applies the inverse of <c>RescaleSlope</c>/<c>RescaleIntercept</c> so the stored
        /// pixel value reproduces the input via <c>(stored * slope) + intercept</c>.
        /// </summary>
        private static byte[] EncodeSlice(
            float[] floatData, int z, int sliceLen,
            double rescaleSlope, double rescaleIntercept,
            bool signedPixels)
        {
            double slope = (rescaleSlope == 0.0) ? 1.0 : rescaleSlope;
            double invSlope = 1.0 / slope;
            int start = z * sliceLen;
            byte[] bytes = new byte[(long)sliceLen * 2];

            double minVal = signedPixels ? (double)short.MinValue : (double)ushort.MinValue;
            double maxVal = signedPixels ? (double)short.MaxValue : (double)ushort.MaxValue;

            for (int i = 0; i < sliceLen; i++)
            {
                double v = floatData[start + i];
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                double stored = (v - rescaleIntercept) * invSlope;
                if (stored < minVal) stored = minVal;
                if (stored > maxVal) stored = maxVal;
                int o = i * 2;
                if (signedPixels)
                {
                    short s = (short)Math.Round(stored);
                    bytes[o]     = (byte)(s & 0xFF);
                    bytes[o + 1] = (byte)((s >> 8) & 0xFF);
                }
                else
                {
                    ushort u = (ushort)Math.Round(stored);
                    bytes[o]     = (byte)(u & 0xFF);
                    bytes[o + 1] = (byte)((u >> 8) & 0xFF);
                }
            }
            return bytes;
        }

        private static DicomDataset BuildImageSliceDataset(
            NiftiPatientMetadata metadata,
            string sopClassUid,
            string sopInstanceUid,
            string seriesInstanceUid,
            string modality,
            string nowDate,
            string nowTime,
            int rows,
            int cols,
            double spX,
            double spY,
            double spZ,
            double ippX,
            double ippY,
            double ippZ,
            string[] iop,
            int instanceNumber,
            bool signedPixels,
            byte[] pixelBytes,
            double sliceLocation)
        {
            var ds = new DicomDataset();

            // SOP common
            ds.AddOrUpdate(DicomTag.SOPClassUID, sopClassUid);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
            ds.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");

            // ImageType is Type 1 (required, non-empty) in the CT / MR / PT Image Module
            // (DICOM PS3.3 C.7.6.1). We mark these as ORIGINAL/PRIMARY/AXIAL even though
            // the pixel data was regenerated from a NIfTI volume: Eclipse (and several
            // other TPSes) refuse to accept an RT-STRUCT whose referenced image series
            // is tagged SECONDARY -- the planning workflow only associates structure
            // sets with PRIMARY image sets. The provenance is still recorded via
            // SeriesDescription ("Generated from NIfTI image") and Manufacturer.
            ds.AddOrUpdate(DicomTag.ImageType, "ORIGINAL", "PRIMARY", "AXIAL");

            // Patient
            ds.AddOrUpdate(DicomTag.PatientID, metadata.PatientId ?? "");
            ds.AddOrUpdate(DicomTag.PatientName, metadata.PatientName ?? "");
            ds.AddOrUpdate(DicomTag.PatientBirthDate, metadata.PatientBirthDate ?? "");
            ds.AddOrUpdate(DicomTag.PatientSex, metadata.PatientSex ?? "");

            // Study
            ds.AddOrUpdate(DicomTag.StudyInstanceUID, metadata.StudyInstanceUid ?? "");
            ds.AddOrUpdate(DicomTag.StudyDate, metadata.StudyDate ?? nowDate);
            ds.AddOrUpdate(DicomTag.StudyTime, metadata.StudyTime ?? nowTime);
            ds.AddOrUpdate(DicomTag.AccessionNumber, metadata.AccessionNumber ?? "");
            ds.AddOrUpdate(DicomTag.ReferringPhysicianName, metadata.ReferringPhysicianName ?? "");
            ds.AddOrUpdate(DicomTag.StudyID, metadata.StudyId ?? "");

            // Series
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesInstanceUid);
            ds.AddOrUpdate(DicomTag.SeriesNumber, 1);
            ds.AddOrUpdate(DicomTag.Modality, modality);
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Generated from NIfTI image");

            // Frame of reference
            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, metadata.FrameOfReferenceUid ?? "");
            ds.AddOrUpdate(DicomTag.PositionReferenceIndicator, "");

            // PatientPosition (0018,5100) is Type 2C — required for CT/MR/PT image storage
            // SOP classes; TPS systems reject the series with "Unknown patient position"
            // when this tag is absent. Falls back to HFS when the metadata field is empty.
            string patientPosition = string.IsNullOrEmpty(metadata.PatientPosition) ? "HFS" : metadata.PatientPosition;
            ds.AddOrUpdate(DicomTag.PatientPosition, patientPosition);

            // General image / instance creation
            ds.AddOrUpdate(DicomTag.InstanceNumber, instanceNumber);
            ds.AddOrUpdate(DicomTag.InstanceCreationDate, nowDate);
            ds.AddOrUpdate(DicomTag.InstanceCreationTime, nowTime);
            ds.AddOrUpdate(DicomTag.ContentDate, nowDate);
            ds.AddOrUpdate(DicomTag.ContentTime, nowTime);
            ds.AddOrUpdate(DicomTag.Manufacturer, "Dicom_RT_images_Csharp");

            // Image plane / pixel module
            ds.AddOrUpdate(DicomTag.Rows, (ushort)rows);
            ds.AddOrUpdate(DicomTag.Columns, (ushort)cols);
            ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
            ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
            ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
            ds.AddOrUpdate(DicomTag.BitsStored, (ushort)16);
            ds.AddOrUpdate(DicomTag.HighBit, (ushort)15);
            ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)(signedPixels ? 1 : 0));

            ds.AddOrUpdate(DicomTag.PixelSpacing, new[] { FormatDs(spY), FormatDs(spX) });
            ds.AddOrUpdate(DicomTag.SliceThickness, FormatDs(spZ));
            ds.AddOrUpdate(DicomTag.SliceLocation, FormatDs(sliceLocation));
            ds.AddOrUpdate(DicomTag.ImagePositionPatient, new[]
            {
                FormatDs(ippX), FormatDs(ippY), FormatDs(ippZ)
            });
            ds.AddOrUpdate(DicomTag.ImageOrientationPatient, iop);

            // Rescale slope / intercept — let the consuming app reconstruct the original
            // physical value via stored * slope + intercept.
            ds.AddOrUpdate(DicomTag.RescaleSlope, FormatDs(metadata.ImageRescaleSlope));
            ds.AddOrUpdate(DicomTag.RescaleIntercept, FormatDs(metadata.ImageRescaleIntercept));

            // Modality-specific required tags. CT Image IOD requires KVP (Type 2);
            // PT Image IOD requires Units (Type 1) and CorrectedImage (Type 2). TPSes
            // and PET viewers reject the series outright when these are missing — the
            // SUV interpretation in particular is undefined without Units.
            switch ((modality ?? "CT").ToUpperInvariant())
            {
                case "CT":
                    ds.AddOrUpdate(DicomTag.KVP, "120");
                    // RescaleType (0028,1054) is Type 3 in the CT Image Module but de
                    // facto required by Eclipse: without it, ARIA treats RescaleSlope/
                    // Intercept as opaque scaling and refuses to associate an RT-STRUCT
                    // with the series (planning workflows need an HU-calibrated CT).
                    ds.AddOrUpdate(DicomTag.RescaleType, "HU");
                    break;
                case "PT":
                    ds.AddOrUpdate(DicomTag.Units, "BQML");
                    ds.AddOrUpdate(DicomTag.CorrectedImage,
                        "DECY", "ATTN", "SCAT", "DTIM", "RAN", "RADL", "DCAL", "SLSENS", "NORM");
                    break;
            }

            // Pixel data: 16-bit little-endian, OW VR.
            ds.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixelBytes)));

            return ds;
        }

        private static string SopClassUidFor(string modality)
        {
            switch ((modality ?? "CT").ToUpperInvariant())
            {
                case "MR": return MrImageStorage;
                case "PT": return PtImageStorage;
                case "CT":
                default: return CtImageStorage;
            }
        }

        private static bool FolderContainsSeries(string folder, string seriesInstanceUid)
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.dcm", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var dcm = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                    string uid = dcm.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "");
                    if (string.Equals(uid, seriesInstanceUid, StringComparison.Ordinal))
                        return true;
                }
                catch (Exception)
                {
                    // ignore unreadable files
                }
            }
            return false;
        }

        private static string ShortUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "(none)";
            return uid.Length <= 12 ? uid : "..." + uid.Substring(uid.Length - 9);
        }

        /// <summary>
        /// Format a double as a DICOM DS (Decimal String) value: at most 16 chars per value.
        /// Mirrors the pattern used in RtStructWriterService / RtDoseWriterService.
        /// </summary>
        private static string FormatDs(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            string s = d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (s.Length <= 16) return s;
            for (int prec = 9; prec >= 1; prec--)
            {
                s = d.ToString("G" + prec, System.Globalization.CultureInfo.InvariantCulture);
                if (s.Length <= 16) return s;
            }
            return ((long)Math.Round(d)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
