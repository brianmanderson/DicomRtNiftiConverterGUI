using System;
using System.Collections.Generic;
using FellowOakDicom;
using itk.simple;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Loads a sorted DICOM image series into a SimpleITK Image with a
    /// direction matrix consistent with the data array layout.
    ///
    /// SimpleITK / ITK's ImageSeriesReader builds the slice-direction column
    /// (3rd column of the direction matrix) as IOP_col × IOP_row, the
    /// negative of the DICOM-standard IOP_row × IOP_col. Combined with an
    /// ascending-IPP file sort, this yields an affine whose 3rd direction
    /// column points opposite to the data layout: voxel (0,0,0) maps to
    /// the correct origin, but voxel (0,0,N-1) maps to a physical position
    /// that does not exist in the scan. RT-STRUCT contour rasterization
    /// and DICOM round-trip both inherit the broken mapping.
    ///
    /// This helper computes the slice-direction column empirically from
    /// (IPP_last − IPP_first) of the sorted endpoints, producing a
    /// canonical affine independent of ITK's internal sign convention
    /// and correct for any IOP (axial / coronal / sagittal / oblique).
    /// </summary>
    internal static class DicomImageSeriesLoader
    {
        // No non-uniform-spacing check lives here any more (S24, W1-C). The one that did
        // compared ‖IPP_last − IPP_first‖ with (N−1)·spacing[2], but ITK's ImageSeriesReader
        // defines spacing[2] as exactly that quotient over the same ordered list, so the two
        // sides could never differ by more than rounding and the guard never fired (a
        // 1/1/3 mm series loads with spacing 5/3, pinned by DicomImageSeriesLoaderTests).
        // SeriesGeometryProbe owns the warning, computed from the per-slice positions before
        // the series is loaded, and RtStructMaskService places contours by ReadPerSliceZ.

        /// <summary>
        /// Loads the series and overrides the 3rd direction-matrix column
        /// with the empirical slice-step unit vector.
        /// </summary>
        public static Image LoadCorrected(List<string> sortedDicomFiles)
        {
            var fileNames = new VectorString();
            foreach (var f in sortedDicomFiles) fileNames.Add(f);

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();
            Image image = reader.Execute();

            // Single-slice volumes have no inter-slice direction to derive.
            if (sortedDicomFiles.Count < 2) return image;

            if (!TryReadIpp(sortedDicomFiles[0], out double[] firstIpp) ||
                !TryReadIpp(sortedDicomFiles[sortedDicomFiles.Count - 1], out double[] lastIpp))
            {
                // Fallback: if either endpoint is unreadable, leave the
                // SimpleITK-reported direction unmodified.
                return image;
            }

            double dx = lastIpp[0] - firstIpp[0];
            double dy = lastIpp[1] - firstIpp[1];
            double dz = lastIpp[2] - firstIpp[2];
            double norm = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Endpoints coincide — degenerate, keep SimpleITK's value.
            if (norm < 1e-6) return image;

            double ux = dx / norm;
            double uy = dy / norm;
            double uz = dz / norm;

            // 3rd direction column lives at indices 2, 5, 8 in the row-major
            // 9-element direction vector. First two columns (in-plane axes
            // from IOP) are correct as-is and untouched.
            var d = image.GetDirection();
            var corrected = new VectorDouble
            {
                d[0], d[1], ux,
                d[3], d[4], uy,
                d[6], d[7], uz
            };
            image.SetDirection(corrected);
            return image;
        }

        private static bool TryReadIpp(string dicomPath, out double[] ipp)
        {
            ipp = null;
            try
            {
                var dcm = DicomFile.Open(dicomPath, FileReadOption.SkipLargeTags);
                if (!dcm.Dataset.Contains(DicomTag.ImagePositionPatient)) return false;
                var values = dcm.Dataset.GetValues<double>(DicomTag.ImagePositionPatient);
                if (values == null || values.Length < 3) return false;
                ipp = new[] { values[0], values[1], values[2] };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the per-slice ``ImagePositionPatient[2]`` (Z in patient
        /// coordinates, mm) for every DICOM file in <paramref name="sortedDicomFiles"/>,
        /// preserving the input file order. Used by RtStructMaskService to map
        /// each RTSTRUCT contour plane's z-mm to the exact slice index, instead
        /// of going through SimpleITK's <c>TransformPhysicalPointToContinuousIndex</c>
        /// which assumes uniform Z spacing.
        ///
        /// On a non-uniform CT acquisition (mixed 3 mm / 6 mm gaps -- common
        /// on NSCLC-Radiomics), the ITK-averaged spacing differs from any
        /// individual slice's true Z position; rounding the
        /// continuous-index Z then lands on the wrong slice for several
        /// contour planes. Building the index from the per-DICOM
        /// <c>ImagePositionPatient[2]</c> directly avoids this.
        ///
        /// Returns <c>null</c> if any file is unreadable; the caller can fall
        /// back to the uniform-spacing path in that case.
        /// </summary>
        internal static double[] ReadPerSliceZ(List<string> sortedDicomFiles)
        {
            if (sortedDicomFiles == null || sortedDicomFiles.Count == 0)
                return null;
            var zs = new double[sortedDicomFiles.Count];
            for (int i = 0; i < sortedDicomFiles.Count; i++)
            {
                if (!TryReadIpp(sortedDicomFiles[i], out double[] ipp))
                {
                    return null;
                }
                zs[i] = ipp[2];
            }
            return zs;
        }
    }
}
