using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FellowOakDicom;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Computes the derived (non-DICOM-tag) values offered in the metadata picker — voxel size,
    /// image dimensions, ROI names/count, max dose. Uses FellowOakDicom only (no SimpleITK) so the
    /// metadata path stays free of native dependencies and unit-testable. Every method is total:
    /// it returns a JSON-ready object (boxed long/double, string[]/long[]/double[]) or null, and
    /// never throws.
    /// </summary>
    public static class MetadataComputedValues
    {
        /// <summary>
        /// Output voxel spacing in mm as [x, y, z]. Prefers <paramref name="precomputedSpacing"/>
        /// (the export-accurate, resample-aware value the caller already has); otherwise derives it
        /// from the slice's PixelSpacing/SliceThickness. Null if neither yields all three components.
        /// </summary>
        public static object ImageVoxelSize(DicomDataset firstSlice, double[] precomputedSpacing)
        {
            if (precomputedSpacing != null && precomputedSpacing.Length == 3)
                return new double[] { precomputedSpacing[0], precomputedSpacing[1], precomputedSpacing[2] };

            return SpacingFromDataset(firstSlice, DicomTag.SliceThickness, null);
        }

        /// <summary>
        /// Image grid size as [columns, rows, slices]. Slice count comes from NumberOfFrames when
        /// the slice is multiframe (&gt; 1), otherwise from <paramref name="sliceFileCount"/>.
        /// </summary>
        public static object ImageDimensions(DicomDataset firstSlice, int sliceFileCount)
        {
            if (firstSlice == null)
                return null;
            try
            {
                long cols = firstSlice.GetSingleValueOrDefault<int>(DicomTag.Columns, 0);
                long rows = firstSlice.GetSingleValueOrDefault<int>(DicomTag.Rows, 0);
                int frames = firstSlice.GetSingleValueOrDefault<int>(DicomTag.NumberOfFrames, 0);
                long z = frames > 1 ? frames : (sliceFileCount > 0 ? sliceFileCount : 1);
                return new long[] { cols, rows, z };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Names of every ROI in the RTSTRUCT's StructureSetROISequence, as a string[].</summary>
        public static object RoiNames(DicomDataset rtStructDataset)
        {
            var names = ExtractRoiNames(rtStructDataset);
            return names == null ? null : (object)names.ToArray();
        }

        /// <summary>Count of ROIs in the RTSTRUCT's StructureSetROISequence, as a boxed long.</summary>
        public static object RoiCount(DicomDataset rtStructDataset)
        {
            var names = ExtractRoiNames(rtStructDataset);
            return names == null ? null : (object)(long)names.Count;
        }

        /// <summary>
        /// Maximum dose value (stored max × DoseGridScaling). Reads the raw PixelData buffer and
        /// interprets it per BitsAllocated (16/32) and PixelRepresentation — fo-dicom's
        /// DicomPixelData does not support 32-bit RTDOSE. Null for encapsulated (compressed) dose,
        /// absent/unsupported pixel data, or any read failure.
        /// </summary>
        public static object MaxDose(DicomDataset rtDoseDataset)
        {
            if (rtDoseDataset == null)
                return null;
            try
            {
                if (rtDoseDataset.InternalTransferSyntax != null && rtDoseDataset.InternalTransferSyntax.IsEncapsulated)
                    return null;
                if (!rtDoseDataset.Contains(DicomTag.PixelData))
                    return null;

                int bitsAllocated = rtDoseDataset.GetSingleValueOrDefault<int>(DicomTag.BitsAllocated, 0);
                int pixelRepresentation = rtDoseDataset.GetSingleValueOrDefault<int>(DicomTag.PixelRepresentation, 0);
                double scaling = rtDoseDataset.GetSingleValueOrDefault<double>(DicomTag.DoseGridScaling, 1.0);
                if (Math.Abs(scaling) < 1e-30)
                    scaling = 1.0;

                var element = rtDoseDataset.GetDicomItem<DicomElement>(DicomTag.PixelData);
                if (element == null || element.Buffer == null)
                    return null;
                byte[] bytes = element.Buffer.Data;
                if (bytes == null || bytes.Length == 0)
                    return null;

                ReadOnlySpan<byte> buffer = bytes;
                double maxStored;
                if (bitsAllocated == 32)
                {
                    if (pixelRepresentation == 1)
                    {
                        var span = MemoryMarshal.Cast<byte, int>(buffer);
                        int m = int.MinValue;
                        for (int i = 0; i < span.Length; i++) if (span[i] > m) m = span[i];
                        maxStored = m;
                    }
                    else
                    {
                        var span = MemoryMarshal.Cast<byte, uint>(buffer);
                        uint m = 0;
                        for (int i = 0; i < span.Length; i++) if (span[i] > m) m = span[i];
                        maxStored = m;
                    }
                }
                else if (bitsAllocated == 16)
                {
                    if (pixelRepresentation == 1)
                    {
                        var span = MemoryMarshal.Cast<byte, short>(buffer);
                        short m = short.MinValue;
                        for (int i = 0; i < span.Length; i++) if (span[i] > m) m = span[i];
                        maxStored = m;
                    }
                    else
                    {
                        var span = MemoryMarshal.Cast<byte, ushort>(buffer);
                        ushort m = 0;
                        for (int i = 0; i < span.Length; i++) if (span[i] > m) m = span[i];
                        maxStored = m;
                    }
                }
                else
                {
                    return null; // unsupported bit depth
                }

                return maxStored * scaling;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Dose grid voxel spacing in mm as [x, y, z]. X/Y come from PixelSpacing; Z from the first
        /// GridFrameOffsetVector step, falling back to SliceThickness. Null if any component is missing.
        /// </summary>
        public static object DoseVoxelSize(DicomDataset rtDoseDataset)
        {
            return SpacingFromDataset(rtDoseDataset, DicomTag.SliceThickness, DicomTag.GridFrameOffsetVector);
        }

        // --- helpers ---------------------------------------------------------------------------

        // Builds [x, y, z] from PixelSpacing (DICOM order is [row(y), column(x)]) plus a Z source.
        // Z is taken from the first step of <paramref name="zVectorTag"/> when supplied (e.g. the
        // dose GridFrameOffsetVector), otherwise from <paramref name="zScalarTag"/> (SliceThickness),
        // which is also the fallback when the vector is unusable. Returns null unless all three are present.
        private static object SpacingFromDataset(DicomDataset ds, DicomTag zScalarTag, DicomTag zVectorTag)
        {
            if (ds == null)
                return null;
            try
            {
                double x = double.NaN, y = double.NaN, z = double.NaN;

                if (ds.Contains(DicomTag.PixelSpacing))
                {
                    var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                    if (ps != null && ps.Length >= 2) { y = ps[0]; x = ps[1]; }
                }

                if (zVectorTag != null && ds.Contains(zVectorTag))
                {
                    var gfov = ds.GetValues<double>(zVectorTag);
                    if (gfov != null && gfov.Length >= 2)
                        z = Math.Abs(gfov[1] - gfov[0]);
                }

                if (double.IsNaN(z) && ds.Contains(zScalarTag))
                    z = ds.GetSingleValueOrDefault<double>(zScalarTag, double.NaN);

                if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
                    return null;
                return new double[] { x, y, z };
            }
            catch
            {
                return null;
            }
        }

        // Returns the ROI names (possibly empty) when StructureSetROISequence is present, else null.
        // Mirrors DicomScannerService.ParseRtStructInfo's walk of the sequence.
        private static List<string> ExtractRoiNames(DicomDataset ds)
        {
            if (ds == null)
                return null;
            try
            {
                if (!ds.Contains(DicomTag.StructureSetROISequence))
                    return null;

                var seq = ds.GetSequence(DicomTag.StructureSetROISequence);
                var names = new List<string>();
                foreach (var item in seq)
                {
                    string roiName = item.GetSingleValueOrDefault<string>(DicomTag.ROIName, "");
                    if (!string.IsNullOrEmpty(roiName))
                        names.Add(roiName);
                }
                return names;
            }
            catch
            {
                return null;
            }
        }
    }
}
