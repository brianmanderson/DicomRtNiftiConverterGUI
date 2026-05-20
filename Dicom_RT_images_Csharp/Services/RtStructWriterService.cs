using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;
using itk.simple;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Builds a new DICOM RT-Structure Set (.dcm) from one or more NIfTI binary masks
    /// referencing an existing CT/MR/PT image series. Each mask file becomes one ROI;
    /// the file's basename (without extension) becomes the ROI name.
    /// </summary>
    public class RtStructWriterService
    {
        // SOP Class UID for RT Structure Set Storage
        private const string RtStructSopClassUid = "1.2.840.10008.5.1.4.1.1.481.3";

        // Cycle palette for ROIDisplayColor (R\G\B)
        private static readonly int[][] ColorPalette = new int[][]
        {
            new[] { 255, 0, 0 },     // red
            new[] { 0, 255, 0 },     // green
            new[] { 0, 128, 255 },   // blue
            new[] { 255, 255, 0 },   // yellow
            new[] { 255, 0, 255 },   // magenta
            new[] { 0, 255, 255 },   // cyan
            new[] { 255, 165, 0 },   // orange
            new[] { 160, 32, 240 }   // purple
        };

        /// <summary>
        /// Discovers <paramref name="dicomFolder"/>/masks/*.nii.gz, builds a single RT-STRUCT
        /// referencing <paramref name="referenceSeries"/>, writes it to <paramref name="outputPath"/>,
        /// and returns the written path. When <paramref name="referenceSeries"/> is null, falls
        /// back to a metadata-driven shell built from <paramref name="metadata"/> with no
        /// per-slice image references.
        /// </summary>
        public string ConvertMasksFolderToRtStruct(
            string dicomFolder,
            DicomSeriesGroup referenceSeries,
            string outputPath,
            IProgress<string> progress,
            CancellationToken ct,
            NiftiPatientMetadata metadata = null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(dicomFolder) || !Directory.Exists(dicomFolder))
                throw new InvalidOperationException("DICOM folder does not exist.");

            string masksDir = Path.Combine(dicomFolder, "masks");
            if (!Directory.Exists(masksDir))
                throw new InvalidOperationException($"Required subfolder not found: {masksDir}");

            var dupeWarnings = new List<string>();
            var maskFiles = NiftiFileNaming.EnumerateNiftiFiles(masksDir, dupeWarnings)
                                           .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                           .ToList();
            foreach (var dup in dupeWarnings)
                progress?.Report($"  Skipping {Path.GetFileName(dup)} — gzipped variant present.");
            if (maskFiles.Count == 0)
                throw new InvalidOperationException($"No .nii or .nii.gz files found in {masksDir}");

            bool hasReferenceSeries = referenceSeries != null
                && referenceSeries.FilePaths != null
                && referenceSeries.FilePaths.Count > 0;

            if (!hasReferenceSeries && metadata == null)
                throw new InvalidOperationException(
                    "Reference image series has no DICOM files and no metadata was provided.");

            Image referenceImage = null;
            Dictionary<int, (string sopClassUid, string sopInstanceUid)> sliceLookup;
            DicomDataset refDs;

            if (hasReferenceSeries)
            {
                // Load reference image series and build slice-Z → SOP UID map
                progress?.Report("Reading reference image series...");
                var sortedDicomFiles = SortFilesBySlicePosition(referenceSeries.FilePaths);
                referenceImage = LoadImageSeries(sortedDicomFiles);
                sliceLookup = BuildSopInstanceLookup(sortedDicomFiles);

                // Open the first DICOM file once to copy patient/study metadata
                var refDicom = DicomFile.Open(sortedDicomFiles[0], FileReadOption.SkipLargeTags);
                refDs = refDicom.Dataset;
            }
            else
            {
                // No reference series — synthesize patient/study tags from metadata.json. Each
                // mask's physical coordinates come from its own Nifti affine, and ContourImage
                // references are omitted (per-contour and at the top level).
                progress?.Report("No reference DICOM series — using metadata.json + each mask's native grid.");
                sliceLookup = new Dictionary<int, (string sopClassUid, string sopInstanceUid)>();
                refDs = new NiftiMetadataService().BuildSyntheticRefDataset(metadata);
            }

            // Build RT-STRUCT dataset shell (patient/study/series/frame-of-ref/referenced-frame)
            var rtDs = BuildRtStructShell(refDs, referenceSeries, sliceLookup, metadata);

            var structureSetROISeq = new DicomSequence(DicomTag.StructureSetROISequence);
            var roiContourSeq = new DicomSequence(DicomTag.ROIContourSequence);
            var observationsSeq = new DicomSequence(DicomTag.RTROIObservationsSequence);

            int roiNumber = 0;
            int roiAdded = 0;
            string frameOfRefUid;
            if (hasReferenceSeries)
            {
                frameOfRefUid = string.IsNullOrEmpty(referenceSeries.FrameOfReferenceUID)
                    ? GetStringTag(refDs, DicomTag.FrameOfReferenceUID, "")
                    : referenceSeries.FrameOfReferenceUID;
            }
            else
            {
                frameOfRefUid = metadata.FrameOfReferenceUid ?? "";
                if (string.IsNullOrEmpty(frameOfRefUid))
                    frameOfRefUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            }

            foreach (var maskPath in maskFiles)
            {
                ct.ThrowIfCancellationRequested();
                roiNumber++;

                string roiName = NiftiFileNaming.StripNiftiExtension(maskPath);

                progress?.Report($"Processing ROI '{roiName}'...");

                Image rawMask;
                try
                {
                    rawMask = SimpleITK.ReadImage(maskPath);
                }
                catch (Exception ex)
                {
                    progress?.Report($"  Failed to read {Path.GetFileName(maskPath)}: {ex.Message}");
                    continue;
                }

                Image alignedMask = referenceImage != null
                    ? EnsureMaskAlignedToReference(rawMask, referenceImage)
                    : rawMask;
                if (!ReferenceEquals(alignedMask, rawMask))
                    rawMask.Dispose();

                // Cast to UInt8 for byte-buffer access
                Image maskU8 = alignedMask;
                if (alignedMask.GetPixelID() != PixelIDValueEnum.sitkUInt8)
                {
                    maskU8 = SimpleITK.Cast(alignedMask, PixelIDValueEnum.sitkUInt8);
                    alignedMask.Dispose();
                }

                var contourSeq = ExtractContoursFromMask(maskU8, sliceLookup, progress, ct);
                maskU8.Dispose();

                if (contourSeq.Items.Count == 0)
                {
                    progress?.Report($"  '{roiName}' has no non-empty slices — skipping.");
                    roiNumber--; // do not consume number
                    continue;
                }

                // StructureSetROI item
                var ssRoi = new DicomDataset
                {
                    { DicomTag.ROINumber, roiNumber },
                    { DicomTag.ReferencedFrameOfReferenceUID, frameOfRefUid },
                    { DicomTag.ROIName, roiName },
                    { DicomTag.ROIGenerationAlgorithm, "MANUAL" }
                };
                structureSetROISeq.Items.Add(ssRoi);

                // ROIContour item
                int[] color = ColorPalette[(roiNumber - 1) % ColorPalette.Length];
                var contourItem = new DicomDataset
                {
                    { DicomTag.ROIDisplayColor, new[] { color[0], color[1], color[2] } },
                    { DicomTag.ReferencedROINumber, roiNumber }
                };
                contourItem.Add(contourSeq);
                roiContourSeq.Items.Add(contourItem);

                // RTROIObservations item. RTROIInterpretedType is Type 2 with defined terms
                // (PS3.3 C.8.8.5); many TPS reject an empty value, so infer from the ROI name.
                var obsItem = new DicomDataset
                {
                    { DicomTag.ObservationNumber, roiNumber },
                    { DicomTag.ReferencedROINumber, roiNumber },
                    { DicomTag.RTROIInterpretedType, InferRtRoiInterpretedType(roiName) },
                    { DicomTag.ROIInterpreter, "" }
                };
                // ROIObservationLabel has VR=SH (16-char cap). Truncate ROI names that exceed
                // the limit -- this tag is Type 3 (optional) so a truncated label is preferable
                // to a validation crash that prevents the whole RTSTRUCT from being written.
                string obsLabel = roiName.Length <= 16 ? roiName : roiName.Substring(0, 16);
                obsItem.Add(new DicomShortString(DicomTag.ROIObservationLabelRETIRED, obsLabel));
                observationsSeq.Items.Add(obsItem);

                roiAdded++;
                progress?.Report($"  '{roiName}' → {contourSeq.Items.Count} contour(s) added.");
            }

            referenceImage?.Dispose();

            if (roiAdded == 0)
                throw new InvalidOperationException("No ROIs could be extracted from the masks (all empty or unreadable).");

            rtDs.AddOrUpdate(structureSetROISeq);
            rtDs.AddOrUpdate(roiContourSeq);
            rtDs.AddOrUpdate(observationsSeq);

            // Save
            string outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            var outFile = new DicomFile(rtDs);
            outFile.Save(outputPath);
            progress?.Report($"Wrote RT-STRUCT: {outputPath} ({roiAdded} ROI(s)).");
            return outputPath;
        }

        // ---------- Internals ----------

        /// <summary>
        /// Reads a sorted list of DICOM image files into a single 3D SimpleITK image
        /// with a corrected direction matrix (see DicomImageSeriesLoader).
        /// </summary>
        private static Image LoadImageSeries(List<string> sortedDicomFiles)
        {
            return DicomImageSeriesLoader.LoadCorrected(sortedDicomFiles);
        }

        /// <summary>
        /// Builds a map keyed by slice z-index (0..N-1, ordered by ImagePositionPatient z)
        /// to (SOPClassUID, SOPInstanceUID).
        /// </summary>
        private static Dictionary<int, (string sopClassUid, string sopInstanceUid)> BuildSopInstanceLookup(
            List<string> sortedDicomFiles)
        {
            var lookup = new Dictionary<int, (string, string)>();
            for (int i = 0; i < sortedDicomFiles.Count; i++)
            {
                try
                {
                    var dcm = DicomFile.Open(sortedDicomFiles[i], FileReadOption.SkipLargeTags);
                    string sopClass = GetStringTag(dcm.Dataset, DicomTag.SOPClassUID, "");
                    string sopInst = GetStringTag(dcm.Dataset, DicomTag.SOPInstanceUID, "");
                    lookup[i] = (sopClass, sopInst);
                }
                catch (Exception)
                {
                    lookup[i] = ("", "");
                }
            }
            return lookup;
        }

        /// <summary>
        /// If the mask has different geometry than the reference, resample it to the reference grid
        /// using linear interpolation followed by a 0.5 threshold. This majority-vote approach
        /// preserves boundary slices that nearest-neighbor would drop when source and target Z
        /// grids are non-aligned (e.g. 2.5 mm prediction → 3.0 mm CT).
        /// </summary>
        private static Image EnsureMaskAlignedToReference(Image mask, Image reference)
        {
            if (GeometriesMatch(mask, reference))
                return mask;

            Image maskF = mask.GetPixelID() == PixelIDValueEnum.sitkFloat32
                ? mask
                : SimpleITK.Cast(mask, PixelIDValueEnum.sitkFloat32);

            var resample = new ResampleImageFilter();
            resample.SetReferenceImage(reference);
            resample.SetInterpolator(InterpolatorEnum.sitkLinear);
            resample.SetDefaultPixelValue(0.0);
            Image resampled = resample.Execute(maskF);

            if (!ReferenceEquals(maskF, mask)) maskF.Dispose();

            Image binary = SimpleITK.BinaryThreshold(resampled, 0.5, double.MaxValue, (byte)1, (byte)0);
            resampled.Dispose();
            return binary;
        }

        private static bool GeometriesMatch(Image a, Image b)
        {
            if (a.GetDimension() != b.GetDimension()) return false;
            var sa = a.GetSize(); var sb = b.GetSize();
            if (sa.Count != sb.Count) return false;
            for (int i = 0; i < sa.Count; i++)
                if (sa[i] != sb[i]) return false;

            const double eps = 1e-4;
            var spa = a.GetSpacing(); var spb = b.GetSpacing();
            for (int i = 0; i < spa.Count; i++)
                if (Math.Abs(spa[i] - spb[i]) > eps) return false;

            var oa = a.GetOrigin(); var ob = b.GetOrigin();
            for (int i = 0; i < oa.Count; i++)
                if (Math.Abs(oa[i] - ob[i]) > eps) return false;

            var da = a.GetDirection(); var db = b.GetDirection();
            for (int i = 0; i < da.Count; i++)
                if (Math.Abs(da[i] - db[i]) > eps) return false;

            return true;
        }

        /// <summary>
        /// Walks the binary mask slice-by-slice. For each slice with non-zero pixels, traces
        /// closed-polygon outlines using marching squares (vertices at pixel-edge midpoints)
        /// and converts each polygon to a ContourSequence DICOM item with physical coordinates.
        /// Marching squares produces simple polygons by construction (no self-intersection)
        /// and emits outer outlines and hole outlines uniformly in a single pass.
        /// </summary>
        private static DicomSequence ExtractContoursFromMask(
            Image maskU8,
            Dictionary<int, (string sopClassUid, string sopInstanceUid)> sliceLookup,
            IProgress<string> progress,
            CancellationToken ct)
        {
            var contourSeq = new DicomSequence(DicomTag.ContourSequence);

            var size = maskU8.GetSize();
            int width = (int)size[0];
            int height = (int)size[1];
            int depth = (int)size[2];
            int sliceLen = width * height;

            // Copy entire mask buffer to managed memory
            byte[] full = new byte[sliceLen * depth];
            IntPtr buf = maskU8.GetBufferAsUInt8();
            Marshal.Copy(buf, full, 0, full.Length);

            int degenerateSkipped = 0;

            for (int z = 0; z < depth; z++)
            {
                ct.ThrowIfCancellationRequested();

                // Quick non-zero check
                int sliceStart = z * sliceLen;
                bool nonEmpty = false;
                for (int k = 0; k < sliceLen; k++)
                    if (full[sliceStart + k] != 0) { nonEmpty = true; break; }
                if (!nonEmpty) continue;

                // Extract this slice into its own array
                byte[] slice = new byte[sliceLen];
                Buffer.BlockCopy(full, sliceStart, slice, 0, sliceLen);

                var polygons = TracePolygonsOnSlice(slice, width, height);
                if (polygons.Count == 0) continue;

                // Resolve referenced SOP for this slice
                string sopClassUid = "";
                string sopInstanceUid = "";
                if (sliceLookup.TryGetValue(z, out var pair))
                {
                    sopClassUid = pair.sopClassUid;
                    sopInstanceUid = pair.sopInstanceUid;
                }

                foreach (var poly in polygons)
                {
                    if (poly.Count < 3) continue;

                    // Reject polygons with no valid surface normal: collinear points and
                    // sub-pixel slivers have zero (or near-zero) shoelace area. Without this
                    // check, 1-pixel-wide protrusions and isolated noise voxels emit
                    // contours that TPS validators (RayStation/Eclipse) discard with a
                    // "contour without a valid normal" warning.
                    if (PolygonPixelArea(poly) < 0.5)
                    {
                        degenerateSkipped++;
                        continue;
                    }

                    // Convert each (col, row) to physical (x, y, z) using the reference geometry
                    var coords = new List<double>(poly.Count * 3);
                    foreach (var (col, row) in poly)
                    {
                        var idxVec = new VectorDouble();
                        idxVec.Add(col);
                        idxVec.Add(row);
                        idxVec.Add(z);
                        var phys = maskU8.TransformContinuousIndexToPhysicalPoint(idxVec);
                        coords.Add(phys[0]);
                        coords.Add(phys[1]);
                        coords.Add(phys[2]);
                    }

                    var contourItem = new DicomDataset
                    {
                        { DicomTag.ContourGeometricType, "CLOSED_PLANAR" },
                        { DicomTag.NumberOfContourPoints, poly.Count }
                    };
                    contourItem.AddOrUpdate(new DicomDecimalString(DicomTag.ContourData,
                        coords.Select(FormatDicomDS).ToArray()));

                    if (!string.IsNullOrEmpty(sopInstanceUid))
                    {
                        var imgRef = new DicomDataset
                        {
                            { DicomTag.ReferencedSOPClassUID, sopClassUid },
                            { DicomTag.ReferencedSOPInstanceUID, sopInstanceUid }
                        };
                        var imgRefSeq = new DicomSequence(DicomTag.ContourImageSequence);
                        imgRefSeq.Items.Add(imgRef);
                        contourItem.Add(imgRefSeq);
                    }

                    contourSeq.Items.Add(contourItem);
                }
            }

            if (degenerateSkipped > 0)
                progress?.Report($"  Skipped {degenerateSkipped} degenerate (collinear/sub-pixel) contour(s).");

            return contourSeq;
        }

        /// <summary>
        /// Returns the absolute 2D pixel-grid area of a closed polygon using the shoelace
        /// formula. Collinear point sets and sub-pixel slivers return 0 (or near-0), which is
        /// the signal we use to skip polygons that have no valid surface normal.
        /// </summary>
        private static double PolygonPixelArea(List<(double col, double row)> poly)
        {
            int n = poly.Count;
            if (n < 3) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                int j = i + 1 == n ? 0 : i + 1;
                sum += poly[i].col * poly[j].row;
                sum -= poly[j].col * poly[i].row;
            }
            return Math.Abs(sum) * 0.5;
        }

        /// <summary>
        /// Traces all closed polygons in a binary slice using the marching-squares algorithm.
        /// Returns a list of polygons, each a list of (col, row) continuous-index coordinates
        /// in walking order. Vertices live at pixel-edge midpoints (half-integer coordinates),
        /// so the contour hugs the foreground/background boundary at sub-pixel precision.
        ///
        /// The mask is conceptually zero-padded by one cell, so foreground regions that touch
        /// the slice edge still yield closed contours. Outer outlines of foreground components
        /// and outlines of any interior holes are emitted uniformly as separate polygons; DICOM
        /// viewers apply the even-odd fill rule, so an inner contour cuts a hole.
        ///
        /// Saddle cases (cell codes 5 and 10, where two foreground pixels are diagonally
        /// opposite within a 2×2 cell) use the "disconnect" rule: each foreground corner is
        /// treated as belonging to its own contour. This guarantees the output polygons are
        /// simple (non-self-intersecting) by construction.
        /// </summary>
        private static List<List<(double col, double row)>> TracePolygonsOnSlice(byte[] slice, int width, int height)
        {
            // Edge identifiers within a cell whose top-left corner is at corner-grid (cx, cy).
            // Vertex coordinates (in continuous-index space):
            //   T (top)    = (cx - 0.5, cy - 1)     bit 0 ↔ bit 1
            //   R (right)  = (cx,       cy - 0.5)   bit 1 ↔ bit 2
            //   B (bottom) = (cx - 0.5, cy)         bit 2 ↔ bit 3
            //   L (left)   = (cx - 1,   cy - 0.5)   bit 3 ↔ bit 0
            // Corner bits: 0=TL pixel(cx-1,cy-1), 1=TR pixel(cx,cy-1), 2=BR pixel(cx,cy), 3=BL pixel(cx-1,cy).
            const int T = 0, R = 1, B = 2, L = 3;

            // Per-case directed segments (from, to). Each cell contributes 0, 1, or 2 segments.
            // Walking direction keeps foreground on the right (image coords, y down).
            // Cases 5 and 10 are saddles and use the disconnect rule: emit the two segments
            // each foreground corner would emit if it were alone, with no shared vertex.
            int[][] caseSegs = new int[16][];
            caseSegs[0]  = new int[0];
            caseSegs[1]  = new int[] { T, L };
            caseSegs[2]  = new int[] { R, T };
            caseSegs[3]  = new int[] { R, L };
            caseSegs[4]  = new int[] { B, R };
            caseSegs[5]  = new int[] { T, L, B, R };  // saddle, disconnect
            caseSegs[6]  = new int[] { B, T };
            caseSegs[7]  = new int[] { B, L };
            caseSegs[8]  = new int[] { L, B };
            caseSegs[9]  = new int[] { T, B };
            caseSegs[10] = new int[] { R, T, L, B };  // saddle, disconnect
            caseSegs[11] = new int[] { R, B };
            caseSegs[12] = new int[] { L, R };
            caseSegs[13] = new int[] { T, R };
            caseSegs[14] = new int[] { L, T };
            caseSegs[15] = new int[0];

            // Adjacency map: from-vertex → to-vertex. Vertex keys use 2× scaling so that
            // half-integer continuous coordinates become exact integers (no float equality issues).
            var adjacency = new Dictionary<(int kc, int kr), (int kc, int kr)>();
            var coords = new Dictionary<(int kc, int kr), (double col, double row)>();

            // Iterate every cell of the corner grid, including a 1-cell border for zero-padding.
            // Cell (cx, cy) has corners at pixel positions (cx-1, cy-1), (cx, cy-1), (cx, cy), (cx-1, cy);
            // pixels outside [0,width)×[0,height) are treated as 0 (background).
            for (int cy = 0; cy <= height; cy++)
            {
                for (int cx = 0; cx <= width; cx++)
                {
                    int tl = SamplePixel(slice, width, height, cx - 1, cy - 1);
                    int tr = SamplePixel(slice, width, height, cx,     cy - 1);
                    int br = SamplePixel(slice, width, height, cx,     cy);
                    int bl = SamplePixel(slice, width, height, cx - 1, cy);
                    int code = tl | (tr << 1) | (br << 2) | (bl << 3);
                    if (code == 0 || code == 15) continue;

                    var segs = caseSegs[code];
                    for (int i = 0; i < segs.Length; i += 2)
                    {
                        int fromEdge = segs[i];
                        int toEdge = segs[i + 1];
                        var fk = EdgeKey(cx, cy, fromEdge);
                        var tk = EdgeKey(cx, cy, toEdge);
                        adjacency[fk] = tk;
                        if (!coords.ContainsKey(fk)) coords[fk] = EdgeXY(cx, cy, fromEdge);
                        if (!coords.ContainsKey(tk)) coords[tk] = EdgeXY(cx, cy, toEdge);
                    }
                }
            }

            // Stitch segments into closed loops by walking the adjacency map.
            var result = new List<List<(double col, double row)>>();
            var visited = new HashSet<(int kc, int kr)>();
            foreach (var start in adjacency.Keys)
            {
                if (visited.Contains(start)) continue;
                var loop = new List<(double col, double row)>();
                var cur = start;
                while (visited.Add(cur))
                {
                    loop.Add(coords[cur]);
                    if (!adjacency.TryGetValue(cur, out var next)) break;
                    cur = next;
                }
                if (loop.Count >= 3) result.Add(loop);
            }
            return result;
        }

        private static int SamplePixel(byte[] slice, int width, int height, int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return 0;
            return slice[y * width + x] != 0 ? 1 : 0;
        }

        private static (int kc, int kr) EdgeKey(int cx, int cy, int edge)
        {
            // 2× the half-integer continuous-index coords, so keys are exact integers.
            //   T midpoint = (cx - 0.5, cy - 1)
            //   R midpoint = (cx,       cy - 0.5)
            //   B midpoint = (cx - 0.5, cy)
            //   L midpoint = (cx - 1,   cy - 0.5)
            switch (edge)
            {
                case 0: return (2 * cx - 1, 2 * cy - 2);   // T
                case 1: return (2 * cx,     2 * cy - 1);   // R
                case 2: return (2 * cx - 1, 2 * cy);       // B
                case 3: return (2 * cx - 2, 2 * cy - 1);   // L
                default: throw new InvalidOperationException("Invalid marching-squares edge id.");
            }
        }

        private static (double col, double row) EdgeXY(int cx, int cy, int edge)
        {
            switch (edge)
            {
                case 0: return (cx - 0.5, cy - 1);     // T
                case 1: return (cx,       cy - 0.5);   // R
                case 2: return (cx - 0.5, cy);         // B
                case 3: return (cx - 1,   cy - 0.5);   // L
                default: throw new InvalidOperationException("Invalid marching-squares edge id.");
            }
        }

        /// <summary>
        /// Builds the RT-STRUCT-level metadata: patient, study (copied from refDs),
        /// new series + SOP instance, and ReferencedFrameOfReferenceSequence with
        /// every CT slice listed in ContourImageSequence.
        /// </summary>
        private static DicomDataset BuildRtStructShell(
            DicomDataset refDs,
            DicomSeriesGroup referenceSeries,
            Dictionary<int, (string sopClassUid, string sopInstanceUid)> sliceLookup,
            NiftiPatientMetadata metadata)
        {
            var ds = new DicomDataset();

            string seriesInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string sopInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string nowDate = DateTime.Now.ToString("yyyyMMdd");
            string nowTime = DateTime.Now.ToString("HHmmss");

            // SOP common
            ds.AddOrUpdate(DicomTag.SOPClassUID, RtStructSopClassUid);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
            ds.AddOrUpdate(DicomTag.MediaStorageSOPClassUID, RtStructSopClassUid);
            ds.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, sopInstanceUid);

            // SpecificCharacterSet is Type 1C in SOP Common; fo-dicom writes string VRs as
            // UTF-8 by default. Declaring ISO_IR 192 keeps Eclipse and other strict readers
            // happy when an ROI name or patient name contains a non-ASCII character.
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

            // RT-STRUCT series (new)
            ds.AddOrUpdate(DicomTag.Modality, "RTSTRUCT");
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesInstanceUid);
            ds.AddOrUpdate(DicomTag.SeriesNumber, 1);
            ds.AddOrUpdate(DicomTag.InstanceNumber, 1);
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Generated by Dicom_RT_images_Csharp");

            // Manufacturer / instance creation
            ds.AddOrUpdate(DicomTag.Manufacturer, "Dicom_RT_images_Csharp");
            ds.AddOrUpdate(DicomTag.InstanceCreationDate, nowDate);
            ds.AddOrUpdate(DicomTag.InstanceCreationTime, nowTime);

            // RT Structure Set module
            ds.AddOrUpdate(DicomTag.StructureSetLabel, "AUTOGEN");
            // StructureSetName (3006,0004) is Type 3 but original Eclipse-style RT-STRUCTs
            // populate it, and some downstream tools (e.g. ARIA) display this in their
            // structure-set list -- write it so the autogenerated set is identifiable.
            ds.AddOrUpdate(DicomTag.StructureSetName, "AUTOGEN");
            ds.AddOrUpdate(DicomTag.StructureSetDate, nowDate);
            ds.AddOrUpdate(DicomTag.StructureSetTime, nowTime);

            // Approval Module is mandatory for the RT Structure Set IOD (PS3.3 A.19);
            // ApprovalStatus is Type 1 (must be present, non-empty). Eclipse / ARIA hang
            // in the Import Summary phase when this tag is missing. UNAPPROVED is the
            // correct default for autogenerated structures that no clinician has signed.
            ds.AddOrUpdate(DicomTag.ApprovalStatus, "UNAPPROVED");

            // Frame of Reference (use refDs's, fall back to series field).
            // NOTE: the RT Structure Set IOD (PS3.3 A.19) does NOT include the Frame of
            // Reference Module at the top level — FrameOfReferenceUID lives only inside
            // ReferencedFrameOfReferenceSequence. Eclipse rejects RT-STRUCTs that carry
            // (0020,0052)/(0020,1040) at the dataset root as non-conformant.
            string frameUid = GetStringTag(refDs, DicomTag.FrameOfReferenceUID,
                referenceSeries?.FrameOfReferenceUID ?? "");
            if (string.IsNullOrEmpty(frameUid))
                frameUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            // ReferencedFrameOfReferenceSequence is nominally Type 3 (User Optional) per
            // PS3.3 A.19, but Eclipse rejects the import with:
            //   * "Frame of reference sequence must contain at least one entry" when the
            //     outer sequence is absent, AND
            //   * "Structure set must contain at least one frame of reference that
            //     references one and only one image series" when the inner study/series
            //     sequence is absent.
            // We therefore always emit BOTH the outer FoR item AND the inner study/series
            // reference. The SeriesInstanceUID is sourced in priority order: (a) the
            // reference series we built from a real DICOM image folder, (b) the
            // ImageSeriesInstanceUid recorded in metadata.json (so the user can attach to
            // an existing series already imported into their TPS by setting it in
            // metadata.json), (c) a fresh synthesized UID as a last resort -- the RT-STRUCT
            // is then structurally valid but the TPS may fail its "resolve referenced
            // series" pre-check until a series with that UID actually exists.
            string refSeriesUid = !string.IsNullOrEmpty(referenceSeries?.SeriesInstanceUID)
                ? referenceSeries.SeriesInstanceUID
                : (!string.IsNullOrEmpty(metadata?.ImageSeriesInstanceUid)
                    ? metadata.ImageSeriesInstanceUid
                    : DicomUIDGenerator.GenerateDerivedFromUUID().UID);

            // Per-slice SOP references come from the actual image series when present,
            // otherwise from metadata (image-less workflow where the user knows the
            // target SOPInstanceUIDs).
            var contourImageSeq = new DicomSequence(DicomTag.ContourImageSequence);
            if (sliceLookup.Count > 0)
            {
                foreach (var kvp in sliceLookup.OrderBy(k => k.Key))
                {
                    if (string.IsNullOrEmpty(kvp.Value.sopInstanceUid)) continue;
                    var imgItem = new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, kvp.Value.sopClassUid },
                        { DicomTag.ReferencedSOPInstanceUID, kvp.Value.sopInstanceUid }
                    };
                    contourImageSeq.Items.Add(imgItem);
                }
            }
            else if (metadata?.ImageSopInstanceUids != null && metadata.ImageSopInstanceUids.Count > 0)
            {
                string sopClassForModality = SopClassUidForModality(metadata.ImageModality);
                foreach (var sopUid in metadata.ImageSopInstanceUids)
                {
                    if (string.IsNullOrEmpty(sopUid)) continue;
                    var imgItem = new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, sopClassForModality },
                        { DicomTag.ReferencedSOPInstanceUID, sopUid }
                    };
                    contourImageSeq.Items.Add(imgItem);
                }
            }

            var rtRefSeriesItem = new DicomDataset
            {
                { DicomTag.SeriesInstanceUID, refSeriesUid }
            };
            // Only attach ContourImageSequence when we have actual SOP references --
            // emitting an empty sequence is ill-formed.
            if (contourImageSeq.Items.Count > 0)
                rtRefSeriesItem.Add(contourImageSeq);

            var rtRefSeriesSeq = new DicomSequence(DicomTag.RTReferencedSeriesSequence);
            rtRefSeriesSeq.Items.Add(rtRefSeriesItem);

            // StudyInstanceUID: prefer the reference dataset's, otherwise fall back to
            // metadata.StudyInstanceUid, otherwise synthesize.
            string studyUid = GetStringTag(refDs, DicomTag.StudyInstanceUID, "");
            if (string.IsNullOrEmpty(studyUid))
                studyUid = metadata?.StudyInstanceUid ?? "";
            if (string.IsNullOrEmpty(studyUid))
                studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            // ReferencedSOPClassUID inside RTReferencedStudySequence identifies the SOP
            // Class of the referenced *study*, not the RT-Struct itself. The conventional
            // value (used by pydicom's reference rtstruct, every TPS-emitted RT-Struct
            // we've seen, and accepted by Eclipse / Aria / RayStation / MIM) is the
            // retired Detached Study Management SOP Class 1.2.840.10008.3.1.2.3.1.
            // Putting the RT Structure Set Storage SOP Class UID here caused Eclipse
            // to flag the structure with a red X on import.
            var rtRefStudyItem = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, "1.2.840.10008.3.1.2.3.1" },
                { DicomTag.ReferencedSOPInstanceUID, studyUid }
            };
            rtRefStudyItem.Add(rtRefSeriesSeq);

            var rtRefStudySeq = new DicomSequence(DicomTag.RTReferencedStudySequence);
            rtRefStudySeq.Items.Add(rtRefStudyItem);

            var refFrameItemTop = new DicomDataset
            {
                { DicomTag.FrameOfReferenceUID, frameUid }
            };
            refFrameItemTop.Add(rtRefStudySeq);

            var refFrameSeq = new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence);
            refFrameSeq.Items.Add(refFrameItemTop);
            ds.AddOrUpdate(refFrameSeq);

            return ds;
        }

        private static string SopClassUidForModality(string modality)
        {
            switch ((modality ?? "CT").ToUpperInvariant())
            {
                case "MR": return "1.2.840.10008.5.1.4.1.1.4";
                case "PT": return "1.2.840.10008.5.1.4.1.1.128";
                case "CT":
                default:   return "1.2.840.10008.5.1.4.1.1.2";
            }
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
            catch (Exception)
            {
                // ignore
            }
        }

        /// <summary>
        /// Heuristically maps an ROI name to a DICOM RT ROI Interpreted Type defined term
        /// (PS3.3 C.8.8.5). Falls back to "ORGAN" when no rule matches. The returned value is
        /// always a non-empty defined term so strict TPS validators (Eclipse, RayStation) accept it.
        /// </summary>
        private static string InferRtRoiInterpretedType(string roiName)
        {
            if (string.IsNullOrWhiteSpace(roiName)) return "ORGAN";
            string n = roiName.Trim().ToUpperInvariant();
            if (n.StartsWith("PTV")) return "PTV";
            if (n.StartsWith("CTV")) return "CTV";
            if (n.StartsWith("GTV")) return "GTV";
            if (n.StartsWith("ITV")) return "CTV";
            if (n == "BODY" || n == "EXTERNAL" || n == "SKIN" || n.Contains("EXTERNAL")) return "EXTERNAL";
            if (n.Contains("AVOID") || n.StartsWith("OAR_") || n.EndsWith("_AVOID")) return "AVOIDANCE";
            if (n.Contains("BOLUS")) return "BOLUS";
            if (n.Contains("MARKER") || n.Contains("FIDUCIAL")) return "MARKER";
            if (n.Contains("ISO") && n.Contains("CENTER")) return "ISOCENTER";
            if (n.Contains("SUPPORT") || n.Contains("COUCH") || n.Contains("TABLE")) return "SUPPORT";
            if (n.Contains("FIXATION") || n.Contains("HEADREST")) return "FIXATION";
            return "ORGAN";
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
        /// Formats a double as a DICOM DS (Decimal String) value. CT/MR coordinates are good
        /// to ~0.001 mm — which is already three orders of magnitude finer than any clinical
        /// scanner's spacing — so we round to 3 decimal places and strip trailing zeros. This
        /// keeps values short (typically 4-8 chars) and avoids the 16-byte DS VR boundary,
        /// where Eclipse's DICOM parser has historically hung trying to read values that hit
        /// the limit exactly. The previous "pick highest precision that fits in 16 chars"
        /// approach emitted float32-noise strings like "-91.399993896484" -- valid DICOM but
        /// fatal to Eclipse's Import-Export Wizard.
        /// </summary>
        private static string FormatDicomDS(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return "0";

            // Standard case: round to 3 decimals, trim trailing zeros via the "0.###" format.
            string s = d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (s.Length <= 16) return s;

            // Extreme-magnitude fallback (>1e13 mm — not anatomically possible, but defensive):
            // fall back through decreasing precision until something fits.
            for (int prec = 9; prec >= 1; prec--)
            {
                s = d.ToString("G" + prec, System.Globalization.CultureInfo.InvariantCulture);
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
    }
}
