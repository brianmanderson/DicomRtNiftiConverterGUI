using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FellowOakDicom;
using itk.simple;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Rasterizes RT Struct contours into binary 3D mask volumes.
    /// Supports all DICOM contour geometry types: CLOSED_PLANAR, OPEN_PLANAR,
    /// OPEN_NONPLANAR, CLOSED_NONPLANAR, and POINT.
    /// </summary>
    public class RtStructMaskService
    {
        /// <summary>
        /// Rasterizes the requested ROIs from an RT Struct file onto the reference image geometry.
        /// </summary>
        /// <param name="rtStructFilePath">Path to the RTSTRUCT DICOM file.</param>
        /// <param name="referenceImage">The CT/MR SimpleITK image defining the output geometry.</param>
        /// <param name="roiNamesToExport">Dictionary mapping output name -> DICOM ROI name.</param>
        /// <param name="progress">Progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary mapping output name -> binary mask Image.</returns>
        public Dictionary<string, Image> RasterizeRois(
            string rtStructFilePath,
            Image referenceImage,
            Dictionary<string, string> roiNamesToExport,
            IProgress<string> progress,
            CancellationToken ct)
        {
            var results = new Dictionary<string, Image>();

            // Parse RTSTRUCT
            var dcmFile = DicomFile.Open(rtStructFilePath);
            var ds = dcmFile.Dataset;

            // Build ROINumber -> ROIName map
            var roiNumberToName = new Dictionary<int, string>();
            if (ds.Contains(DicomTag.StructureSetROISequence))
            {
                foreach (var item in ds.GetSequence(DicomTag.StructureSetROISequence))
                {
                    try
                    {
                        int num = item.GetSingleValue<int>(DicomTag.ROINumber);
                        string name = item.GetSingleValueOrDefault(DicomTag.ROIName, "");
                        roiNumberToName[num] = name;
                    }
                    catch (Exception)
                    {
                        // Skip malformed entries
                    }
                }
            }

            // Build reverse lookup: DICOM ROI name -> output name
            var dicomNameToOutputName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in roiNamesToExport)
            {
                dicomNameToOutputName[kvp.Value] = kvp.Key;
            }

            // Get reference image geometry
            VectorUInt32 size = referenceImage.GetSize();
            int cols = (int)size[0];   // x dimension
            int rows = (int)size[1];   // y dimension
            int slices = (int)size[2]; // z dimension

            if (!ds.Contains(DicomTag.ROIContourSequence))
            {
                return results;
            }

            // Process each ROI contour
            foreach (var roiContour in ds.GetSequence(DicomTag.ROIContourSequence))
            {
                ct.ThrowIfCancellationRequested();

                int refRoiNum;
                try
                {
                    refRoiNum = roiContour.GetSingleValue<int>(DicomTag.ReferencedROINumber);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!roiNumberToName.ContainsKey(refRoiNum)) continue;
                string dicomRoiName = roiNumberToName[refRoiNum];

                if (!dicomNameToOutputName.ContainsKey(dicomRoiName)) continue;
                string outputName = dicomNameToOutputName[dicomRoiName];

                progress?.Report($"    Rasterizing ROI: {dicomRoiName}");

                if (!roiContour.Contains(DicomTag.ContourSequence)) continue;

                DicomSequence contourSeq;
                try
                {
                    contourSeq = roiContour.GetSequence(DicomTag.ContourSequence);
                }
                catch (Exception)
                {
                    continue;
                }

                // Allocate 3D mask: flat byte array [z * rows * cols + y * cols + x]
                byte[] maskData = new byte[slices * rows * cols];
                int contourCount = 0;

                foreach (var contour in contourSeq)
                {
                    ct.ThrowIfCancellationRequested();

                    string geoType = "CLOSED_PLANAR";
                    try
                    {
                        geoType = contour.GetSingleValueOrDefault(DicomTag.ContourGeometricType, "CLOSED_PLANAR");
                    }
                    catch (Exception)
                    {
                        // Default to CLOSED_PLANAR
                    }

                    geoType = (geoType ?? "CLOSED_PLANAR").Trim().ToUpperInvariant();

                    double[] contourData;
                    try
                    {
                        contourData = contour.GetValues<double>(DicomTag.ContourData);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (contourData == null || contourData.Length < 3) continue;

                    bool handled;
                    switch (geoType)
                    {
                        case "CLOSED_PLANAR":
                            handled = RasterizeClosedPlanar(contourData, referenceImage, maskData, rows, cols, slices);
                            break;
                        case "OPEN_PLANAR":
                            handled = RasterizeOpenPlanar(contourData, referenceImage, maskData, rows, cols, slices);
                            break;
                        case "OPEN_NONPLANAR":
                            handled = RasterizeOpenNonplanar(contourData, referenceImage, maskData, rows, cols, slices);
                            break;
                        case "CLOSED_NONPLANAR":
                            handled = RasterizeClosedNonplanar(contourData, referenceImage, maskData, rows, cols, slices);
                            break;
                        case "POINT":
                            handled = RasterizePoint(contourData, referenceImage, maskData, rows, cols, slices);
                            break;
                        default:
                            // Unknown geometry type — skip
                            handled = false;
                            break;
                    }

                    if (handled) contourCount++;
                }

                if (contourCount == 0)
                {
                    progress?.Report($"    Skipping ROI '{dicomRoiName}': no valid contours");
                    continue;
                }

                // Create SimpleITK Image from mask data
                Image maskImage = CreateMaskImage(maskData, referenceImage, cols, rows, slices);
                results[outputName] = maskImage;
            }

            return results;
        }

        // ────────────────────────────────────────────────────────────────
        //  CLOSED_PLANAR — closed polygon on a single slice, scanline fill
        // ────────────────────────────────────────────────────────────────

        private bool RasterizeClosedPlanar(
            double[] contourData, Image referenceImage,
            byte[] maskData, int rows, int cols, int slices)
        {
            if (contourData.Length < 9) return false; // need >= 3 points

            int pointCount = contourData.Length / 3;
            double[] polyX = new double[pointCount];
            double[] polyY = new double[pointCount];
            double sliceZSum = 0;

            for (int i = 0; i < pointCount; i++)
            {
                var idx = PhysicalToIndex(contourData, i, referenceImage);
                if (idx == null) return false;
                polyX[i] = idx[0];
                polyY[i] = idx[1];
                sliceZSum += idx[2];
            }

            int sliceIdx = (int)Math.Round(sliceZSum / pointCount);
            if (sliceIdx < 0 || sliceIdx >= slices) return false;

            ScanlineFillPolygon(maskData, polyX, polyY, pointCount, sliceIdx, rows, cols);
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        //  OPEN_PLANAR — open polyline on a single slice, line rasterization
        // ────────────────────────────────────────────────────────────────

        private bool RasterizeOpenPlanar(
            double[] contourData, Image referenceImage,
            byte[] maskData, int rows, int cols, int slices)
        {
            if (contourData.Length < 6) return false; // need >= 2 points

            int pointCount = contourData.Length / 3;
            double[] polyX = new double[pointCount];
            double[] polyY = new double[pointCount];
            double sliceZSum = 0;

            for (int i = 0; i < pointCount; i++)
            {
                var idx = PhysicalToIndex(contourData, i, referenceImage);
                if (idx == null) return false;
                polyX[i] = idx[0];
                polyY[i] = idx[1];
                sliceZSum += idx[2];
            }

            int sliceIdx = (int)Math.Round(sliceZSum / pointCount);
            if (sliceIdx < 0 || sliceIdx >= slices) return false;

            // Draw line segments between consecutive points (not closed)
            for (int i = 0; i < pointCount - 1; i++)
            {
                RasterizeLine2D(maskData, polyX[i], polyY[i], polyX[i + 1], polyY[i + 1],
                    sliceIdx, rows, cols);
            }
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        //  OPEN_NONPLANAR — open polyline spanning multiple slices
        // ────────────────────────────────────────────────────────────────

        private bool RasterizeOpenNonplanar(
            double[] contourData, Image referenceImage,
            byte[] maskData, int rows, int cols, int slices)
        {
            if (contourData.Length < 6) return false;

            int pointCount = contourData.Length / 3;
            var points = new double[pointCount][];

            for (int i = 0; i < pointCount; i++)
            {
                points[i] = PhysicalToIndex(contourData, i, referenceImage);
                if (points[i] == null) return false;
            }

            // Draw 3D line segments between consecutive points
            for (int i = 0; i < pointCount - 1; i++)
            {
                RasterizeLine3D(maskData, points[i], points[i + 1], rows, cols, slices);
            }
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        //  CLOSED_NONPLANAR — closed polygon spanning multiple slices
        //  Slice the 3D polygon at each axial plane it intersects,
        //  then scanline-fill the resulting cross-section.
        // ────────────────────────────────────────────────────────────────

        private bool RasterizeClosedNonplanar(
            double[] contourData, Image referenceImage,
            byte[] maskData, int rows, int cols, int slices)
        {
            if (contourData.Length < 9) return false;

            int pointCount = contourData.Length / 3;
            var points = new double[pointCount][];

            double minZ = double.MaxValue;
            double maxZ = double.MinValue;

            for (int i = 0; i < pointCount; i++)
            {
                points[i] = PhysicalToIndex(contourData, i, referenceImage);
                if (points[i] == null) return false;
                if (points[i][2] < minZ) minZ = points[i][2];
                if (points[i][2] > maxZ) maxZ = points[i][2];
            }

            int sliceMin = Math.Max(0, (int)Math.Floor(minZ));
            int sliceMax = Math.Min(slices - 1, (int)Math.Ceiling(maxZ));

            bool anyFilled = false;

            // For each slice the polygon spans, compute the cross-section
            for (int sz = sliceMin; sz <= sliceMax; sz++)
            {
                double planeZ = sz + 0.5; // slice center

                // Find intersections of each 3D edge with this z-plane
                var crossX = new List<double>();
                var crossY = new List<double>();

                for (int i = 0; i < pointCount; i++)
                {
                    int j = (i + 1) % pointCount;
                    double z0 = points[i][2];
                    double z1 = points[j][2];

                    // Check if this edge crosses the slice plane
                    if ((z0 <= planeZ && z1 > planeZ) || (z1 <= planeZ && z0 > planeZ))
                    {
                        double t = (planeZ - z0) / (z1 - z0);
                        crossX.Add(points[i][0] + t * (points[j][0] - points[i][0]));
                        crossY.Add(points[i][1] + t * (points[j][1] - points[i][1]));
                    }
                    // If a vertex sits exactly on the plane, include it
                    else if (Math.Abs(z0 - planeZ) < 0.001)
                    {
                        crossX.Add(points[i][0]);
                        crossY.Add(points[i][1]);
                    }
                }

                if (crossX.Count < 3)
                {
                    // Fewer than 3 intersection points — not enough for a polygon.
                    // If there are exactly 2 points, draw a line between them.
                    if (crossX.Count == 2)
                    {
                        RasterizeLine2D(maskData, crossX[0], crossY[0], crossX[1], crossY[1],
                            sz, rows, cols);
                        anyFilled = true;
                    }
                    continue;
                }

                // Order intersection points by angle from centroid to form a valid polygon
                double cx = 0, cy = 0;
                for (int i = 0; i < crossX.Count; i++)
                {
                    cx += crossX[i];
                    cy += crossY[i];
                }
                cx /= crossX.Count;
                cy /= crossY.Count;

                var ordered = new List<int>(Enumerable.Range(0, crossX.Count));
                ordered.Sort((a, b) =>
                {
                    double angA = Math.Atan2(crossY[a] - cy, crossX[a] - cx);
                    double angB = Math.Atan2(crossY[b] - cy, crossX[b] - cx);
                    return angA.CompareTo(angB);
                });

                double[] polyX = new double[ordered.Count];
                double[] polyY = new double[ordered.Count];
                for (int i = 0; i < ordered.Count; i++)
                {
                    polyX[i] = crossX[ordered[i]];
                    polyY[i] = crossY[ordered[i]];
                }

                ScanlineFillPolygon(maskData, polyX, polyY, ordered.Count, sz, rows, cols);
                anyFilled = true;
            }

            return anyFilled;
        }

        // ────────────────────────────────────────────────────────────────
        //  POINT — one or more marker points, set nearest voxel(s)
        // ────────────────────────────────────────────────────────────────

        private bool RasterizePoint(
            double[] contourData, Image referenceImage,
            byte[] maskData, int rows, int cols, int slices)
        {
            int pointCount = contourData.Length / 3;
            bool any = false;

            for (int i = 0; i < pointCount; i++)
            {
                var idx = PhysicalToIndex(contourData, i, referenceImage);
                if (idx == null) continue;

                int x = (int)Math.Round(idx[0]);
                int y = (int)Math.Round(idx[1]);
                int z = (int)Math.Round(idx[2]);

                if (x < 0 || x >= cols || y < 0 || y >= rows || z < 0 || z >= slices)
                    continue;

                maskData[z * rows * cols + y * cols + x] = 1;
                any = true;
            }

            return any;
        }

        // ────────────────────────────────────────────────────────────────
        //  Shared helpers
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Transforms the i-th physical point in contourData to a continuous voxel index.
        /// Returns [x, y, z] or null on failure.
        /// </summary>
        private double[] PhysicalToIndex(double[] contourData, int pointIndex, Image referenceImage)
        {
            int offset = pointIndex * 3;
            var physPt = new VectorDouble(new double[]
            {
                contourData[offset],
                contourData[offset + 1],
                contourData[offset + 2]
            });

            try
            {
                VectorDouble idx = referenceImage.TransformPhysicalPointToContinuousIndex(physPt);
                return new double[] { idx[0], idx[1], idx[2] };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Rasterizes a polygon onto a 2D slice of the 3D mask array using scanline fill with XOR.
        /// Uses floating-point polygon vertices for sub-voxel accuracy.
        /// </summary>
        private void ScanlineFillPolygon(
            byte[] maskData,
            double[] polyX,
            double[] polyY,
            int pointCount,
            int sliceIdx,
            int rows,
            int cols)
        {
            int sliceOffset = sliceIdx * rows * cols;

            // Find Y bounds
            double minYd = double.MaxValue, maxYd = double.MinValue;
            for (int i = 0; i < pointCount; i++)
            {
                if (polyY[i] < minYd) minYd = polyY[i];
                if (polyY[i] > maxYd) maxYd = polyY[i];
            }

            int minY = Math.Max(0, (int)Math.Floor(minYd));
            int maxY = Math.Min(rows - 1, (int)Math.Ceiling(maxYd));

            for (int y = minY; y <= maxY; y++)
            {
                // Scanline at y + 0.5 (pixel center)
                double scanY = y + 0.5;

                // Find all X intersections of polygon edges with this scanline
                var intersections = new List<double>();

                for (int i = 0, j = pointCount - 1; i < pointCount; j = i++)
                {
                    double yi = polyY[i];
                    double yj = polyY[j];

                    // Does this edge cross the scanline?
                    if ((yi <= scanY && yj > scanY) || (yj <= scanY && yi > scanY))
                    {
                        double xIntersect = polyX[i] + (scanY - yi) / (yj - yi) * (polyX[j] - polyX[i]);
                        intersections.Add(xIntersect);
                    }
                }

                // Sort intersections
                intersections.Sort();

                // Fill between pairs (even-odd rule with XOR)
                int rowOffset = sliceOffset + y * cols;
                for (int k = 0; k + 1 < intersections.Count; k += 2)
                {
                    int xStart = Math.Max(0, (int)Math.Ceiling(intersections[k]));
                    int xEnd = Math.Min(cols - 1, (int)Math.Floor(intersections[k + 1]));

                    for (int x = xStart; x <= xEnd; x++)
                    {
                        maskData[rowOffset + x] ^= 1;
                    }
                }
            }
        }

        /// <summary>
        /// Draws a 2D line on a single slice using Bresenham's algorithm.
        /// Sets voxels to 1 (no XOR — lines should accumulate, not toggle).
        /// </summary>
        private void RasterizeLine2D(
            byte[] maskData,
            double x0d, double y0d, double x1d, double y1d,
            int sliceIdx,
            int rows, int cols)
        {
            int x0 = (int)Math.Round(x0d);
            int y0 = (int)Math.Round(y0d);
            int x1 = (int)Math.Round(x1d);
            int y1 = (int)Math.Round(y1d);
            int sliceOffset = sliceIdx * rows * cols;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < cols && y0 >= 0 && y0 < rows)
                {
                    maskData[sliceOffset + y0 * cols + x0] = 1;
                }

                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>
        /// Draws a 3D line between two voxel-space points using 3D Bresenham's algorithm.
        /// Sets voxels to 1.
        /// </summary>
        private void RasterizeLine3D(
            byte[] maskData,
            double[] p0, double[] p1,
            int rows, int cols, int slices)
        {
            int x0 = (int)Math.Round(p0[0]);
            int y0 = (int)Math.Round(p0[1]);
            int z0 = (int)Math.Round(p0[2]);
            int x1 = (int)Math.Round(p1[0]);
            int y1 = (int)Math.Round(p1[1]);
            int z1 = (int)Math.Round(p1[2]);

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int sz = z0 < z1 ? 1 : -1;

            // Determine the dominant axis for 3D Bresenham
            int dm = Math.Max(dx, Math.Max(dy, dz));

            if (dm == 0)
            {
                // Single point
                if (x0 >= 0 && x0 < cols && y0 >= 0 && y0 < rows && z0 >= 0 && z0 < slices)
                    maskData[z0 * rows * cols + y0 * cols + x0] = 1;
                return;
            }

            // DDA stepping along the longest axis for accuracy
            int steps = dm;
            double stepX = (double)(x1 - x0) / steps;
            double stepY = (double)(y1 - y0) / steps;
            double stepZ = (double)(z1 - z0) / steps;

            for (int i = 0; i <= steps; i++)
            {
                int x = (int)Math.Round(x0 + stepX * i);
                int y = (int)Math.Round(y0 + stepY * i);
                int z = (int)Math.Round(z0 + stepZ * i);

                if (x >= 0 && x < cols && y >= 0 && y < rows && z >= 0 && z < slices)
                {
                    maskData[z * rows * cols + y * cols + x] = 1;
                }
            }
        }

        /// <summary>
        /// Creates a SimpleITK Image from a flat byte array, copying geometry from the reference image.
        /// </summary>
        private Image CreateMaskImage(byte[] maskData, Image referenceImage, int cols, int rows, int slices)
        {
            Image mask = new Image((uint)cols, (uint)rows, (uint)slices, PixelIDValueEnum.sitkUInt8);

            // Copy voxel data into the SimpleITK buffer
            IntPtr buffer = mask.GetBufferAsUInt8();
            Marshal.Copy(maskData, 0, buffer, maskData.Length);

            // Copy spatial information from reference image
            mask.SetOrigin(referenceImage.GetOrigin());
            mask.SetSpacing(referenceImage.GetSpacing());
            mask.SetDirection(referenceImage.GetDirection());

            return mask;
        }
    }
}
