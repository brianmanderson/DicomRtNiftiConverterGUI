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
    /// Rasterizes RT Struct contours into binary 3D mask volumes using a scanline fill algorithm.
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

                    if (geoType != "CLOSED_PLANAR") continue;

                    double[] contourData;
                    try
                    {
                        contourData = contour.GetValues<double>(DicomTag.ContourData);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (contourData == null || contourData.Length < 9) continue; // Need at least 3 points

                    int pointCount = contourData.Length / 3;
                    double[] polyXd = new double[pointCount];
                    double[] polyYd = new double[pointCount];
                    double sliceZSum = 0;
                    bool validContour = true;

                    for (int i = 0; i < pointCount; i++)
                    {
                        double px = contourData[i * 3];
                        double py = contourData[i * 3 + 1];
                        double pz = contourData[i * 3 + 2];

                        // Transform physical point to continuous index
                        var physPt = new VectorDouble(new double[] { px, py, pz });
                        VectorDouble continuousIdx;
                        try
                        {
                            continuousIdx = referenceImage.TransformPhysicalPointToContinuousIndex(physPt);
                        }
                        catch (Exception)
                        {
                            validContour = false;
                            break;
                        }

                        polyXd[i] = continuousIdx[0]; // column (x)
                        polyYd[i] = continuousIdx[1]; // row (y)
                        sliceZSum += continuousIdx[2]; // slice (z)
                    }

                    if (!validContour) continue;

                    // Determine slice index (average z, round to nearest)
                    int sliceIdx = (int)Math.Round(sliceZSum / pointCount);

                    // Clamp slice index
                    if (sliceIdx < 0 || sliceIdx >= slices)
                    {
                        continue;
                    }

                    // Run scanline fill with XOR (even-odd rule)
                    ScanlineFillPolygon(maskData, polyXd, polyYd, pointCount, sliceIdx, rows, cols);
                    contourCount++;
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
