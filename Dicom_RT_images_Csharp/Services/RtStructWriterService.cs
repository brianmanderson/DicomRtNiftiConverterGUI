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
        /// and returns the written path.
        /// </summary>
        public string ConvertMasksFolderToRtStruct(
            string dicomFolder,
            DicomSeriesGroup referenceSeries,
            string outputPath,
            IProgress<string> progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(dicomFolder) || !Directory.Exists(dicomFolder))
                throw new InvalidOperationException("DICOM folder does not exist.");

            string masksDir = Path.Combine(dicomFolder, "masks");
            if (!Directory.Exists(masksDir))
                throw new InvalidOperationException($"Required subfolder not found: {masksDir}");

            var maskFiles = Directory.EnumerateFiles(masksDir, "*.nii.gz", SearchOption.TopDirectoryOnly)
                                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                     .ToList();
            if (maskFiles.Count == 0)
                throw new InvalidOperationException($"No .nii.gz files found in {masksDir}");

            if (referenceSeries == null || referenceSeries.FilePaths == null || referenceSeries.FilePaths.Count == 0)
                throw new InvalidOperationException("Reference image series has no DICOM files.");

            // Load reference image series and build slice-Z → SOP UID map
            progress?.Report("Reading reference image series...");
            var sortedDicomFiles = SortFilesBySlicePosition(referenceSeries.FilePaths);
            var referenceImage = LoadImageSeries(sortedDicomFiles);
            var sliceLookup = BuildSopInstanceLookup(sortedDicomFiles);

            // Open the first DICOM file once to copy patient/study metadata
            var refDicom = DicomFile.Open(sortedDicomFiles[0], FileReadOption.SkipLargeTags);
            var refDs = refDicom.Dataset;

            // Build RT-STRUCT dataset shell (patient/study/series/frame-of-ref/referenced-frame)
            var rtDs = BuildRtStructShell(refDs, referenceSeries, sliceLookup);

            var structureSetROISeq = new DicomSequence(DicomTag.StructureSetROISequence);
            var roiContourSeq = new DicomSequence(DicomTag.ROIContourSequence);
            var observationsSeq = new DicomSequence(DicomTag.RTROIObservationsSequence);

            int roiNumber = 0;
            int roiAdded = 0;
            string frameOfRefUid = string.IsNullOrEmpty(referenceSeries.FrameOfReferenceUID)
                ? GetStringTag(refDs, DicomTag.FrameOfReferenceUID, "")
                : referenceSeries.FrameOfReferenceUID;

            foreach (var maskPath in maskFiles)
            {
                ct.ThrowIfCancellationRequested();
                roiNumber++;

                string roiName = Path.GetFileName(maskPath);
                if (roiName.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
                    roiName = roiName.Substring(0, roiName.Length - ".nii.gz".Length);
                else
                    roiName = Path.GetFileNameWithoutExtension(roiName);

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

                Image alignedMask = EnsureMaskAlignedToReference(rawMask, referenceImage);
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

            referenceImage.Dispose();

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
        /// Reads a sorted list of DICOM image files into a single 3D SimpleITK image.
        /// </summary>
        private static Image LoadImageSeries(List<string> sortedDicomFiles)
        {
            var fileNames = new VectorString();
            foreach (var f in sortedDicomFiles) fileNames.Add(f);

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();
            return reader.Execute();
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
        /// closed-polygon outlines (Moore-neighborhood boundary tracing per connected component)
        /// and converts each polygon to a ContourSequence DICOM item with physical coordinates.
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

            return contourSeq;
        }

        /// <summary>
        /// Traces all outer polygons in a binary slice using Moore-neighborhood boundary tracing.
        /// Returns a list of polygons, each a list of (col, row) pixel coordinates in order.
        /// Outer boundaries of foreground components AND boundaries of any interior holes
        /// (background regions enclosed by foreground) are emitted as separate polygons.
        /// DICOM viewers apply the even-odd fill rule, so an inner contour cuts a hole.
        /// </summary>
        private static List<List<(int col, int row)>> TracePolygonsOnSlice(byte[] slice, int width, int height)
        {
            var result = new List<List<(int, int)>>();
            bool[] visitedComponent = new bool[width * height];

            // 8-neighborhood, starting from "left" of the entry direction (counter-clockwise)
            int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 };
            int[] dy = { 0, -1, -1, -1, 0, 1, 1, 1 };

            // 1. Outer boundaries: trace each foreground connected component
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (slice[idx] == 0 || visitedComponent[idx]) continue;

                    // Starting pixel found. Mark this component as visited via flood fill.
                    FloodFillMark(slice, visitedComponent, x, y, width, height);

                    // Now trace its outer boundary starting from (x, y).
                    var poly = TraceOneBoundary(slice, x, y, width, height, dx, dy);
                    if (poly.Count >= 3)
                        result.Add(poly);
                }
            }

            // 2. Holes: build a mask where 1 = background pixel that is NOT reachable
            //    from the slice border via 4-connected background-flood. These are enclosed holes.
            byte[] holeMask = BuildHoleMask(slice, width, height);

            // 3. Each connected hole component → its own boundary polygon (treated as foreground in holeMask).
            bool[] holeVisited = new bool[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (holeMask[idx] == 0 || holeVisited[idx]) continue;

                    FloodFillMark(holeMask, holeVisited, x, y, width, height);
                    var poly = TraceOneBoundary(holeMask, x, y, width, height, dx, dy);
                    if (poly.Count >= 3)
                        result.Add(poly);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a byte array the same size as <paramref name="slice"/> where each cell is 1
        /// iff the corresponding pixel is background (slice == 0) and is NOT reachable from any
        /// border pixel via 4-connected background. These cells are interior holes.
        /// </summary>
        private static byte[] BuildHoleMask(byte[] slice, int width, int height)
        {
            int n = width * height;
            bool[] reachableFromBorder = new bool[n];
            var queue = new Queue<int>();

            // Seed with border background pixels
            for (int x = 0; x < width; x++)
            {
                int top = x;
                if (slice[top] == 0) { reachableFromBorder[top] = true; queue.Enqueue(top); }
                int bot = (height - 1) * width + x;
                if (slice[bot] == 0) { reachableFromBorder[bot] = true; queue.Enqueue(bot); }
            }
            for (int y = 0; y < height; y++)
            {
                int left = y * width;
                if (slice[left] == 0) { reachableFromBorder[left] = true; queue.Enqueue(left); }
                int right = y * width + (width - 1);
                if (slice[right] == 0) { reachableFromBorder[right] = true; queue.Enqueue(right); }
            }

            // 4-connected flood through background
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int y = idx / width;
                int x = idx - y * width;

                int[] nxs = { x - 1, x + 1, x, x };
                int[] nys = { y, y, y - 1, y + 1 };
                for (int i = 0; i < 4; i++)
                {
                    int nx = nxs[i], ny = nys[i];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int nidx = ny * width + nx;
                    if (slice[nidx] != 0) continue;          // foreground blocks the flood
                    if (reachableFromBorder[nidx]) continue; // already visited
                    reachableFromBorder[nidx] = true;
                    queue.Enqueue(nidx);
                }
            }

            byte[] holeMask = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (slice[i] == 0 && !reachableFromBorder[i]) holeMask[i] = 1;
            }
            return holeMask;
        }

        private static void FloodFillMark(byte[] slice, bool[] visited, int sx, int sy, int width, int height)
        {
            var stack = new Stack<(int x, int y)>();
            stack.Push((sx, sy));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                int i = y * width + x;
                if (visited[i] || slice[i] == 0) continue;
                visited[i] = true;
                stack.Push((x + 1, y));
                stack.Push((x - 1, y));
                stack.Push((x, y + 1));
                stack.Push((x, y - 1));
            }
        }

        private static List<(int col, int row)> TraceOneBoundary(
            byte[] slice, int startX, int startY, int width, int height, int[] dx, int[] dy)
        {
            var poly = new List<(int, int)>();
            int cx = startX, cy = startY;
            int prevDir = 6; // we entered from "above" (came from y-1 → +y), so previous direction was south
            poly.Add((cx, cy));

            const int maxSteps = 500_000;
            for (int step = 0; step < maxSteps; step++)
            {
                int searchStart = (prevDir + 6) % 8; // start search rotating CCW from "back-left"
                bool found = false;
                for (int t = 0; t < 8; t++)
                {
                    int dir = (searchStart + t) % 8;
                    int nx = cx + dx[dir];
                    int ny = cy + dy[dir];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    if (slice[ny * width + nx] == 0) continue;

                    cx = nx; cy = ny;
                    prevDir = dir;
                    found = true;
                    break;
                }

                if (!found) break;
                if (cx == startX && cy == startY) break;
                poly.Add((cx, cy));
            }
            return poly;
        }

        /// <summary>
        /// Builds the RT-STRUCT-level metadata: patient, study (copied from refDs),
        /// new series + SOP instance, and ReferencedFrameOfReferenceSequence with
        /// every CT slice listed in ContourImageSequence.
        /// </summary>
        private static DicomDataset BuildRtStructShell(
            DicomDataset refDs,
            DicomSeriesGroup referenceSeries,
            Dictionary<int, (string sopClassUid, string sopInstanceUid)> sliceLookup)
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
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Generated by Dicom_RT_images_Csharp");

            // Manufacturer / instance creation
            ds.AddOrUpdate(DicomTag.Manufacturer, "Dicom_RT_images_Csharp");
            ds.AddOrUpdate(DicomTag.InstanceCreationDate, nowDate);
            ds.AddOrUpdate(DicomTag.InstanceCreationTime, nowTime);

            // RT Structure Set module
            ds.AddOrUpdate(DicomTag.StructureSetLabel, "AUTOGEN");
            ds.AddOrUpdate(DicomTag.StructureSetDate, nowDate);
            ds.AddOrUpdate(DicomTag.StructureSetTime, nowTime);

            // Frame of Reference (use refDs's, fall back to series field)
            string frameUid = GetStringTag(refDs, DicomTag.FrameOfReferenceUID,
                referenceSeries.FrameOfReferenceUID ?? "");
            if (string.IsNullOrEmpty(frameUid))
                frameUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, frameUid);
            ds.AddOrUpdate(DicomTag.PositionReferenceIndicator, "");

            // ReferencedFrameOfReferenceSequence
            // -> RTReferencedStudySequence -> RTReferencedSeriesSequence -> ContourImageSequence
            var contourImageSeq = new DicomSequence(DicomTag.ContourImageSequence);
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

            var rtRefSeriesItem = new DicomDataset
            {
                { DicomTag.SeriesInstanceUID, referenceSeries.SeriesInstanceUID }
            };
            rtRefSeriesItem.Add(contourImageSeq);

            var rtRefSeriesSeq = new DicomSequence(DicomTag.RTReferencedSeriesSequence);
            rtRefSeriesSeq.Items.Add(rtRefSeriesItem);

            string studyUid = GetStringTag(refDs, DicomTag.StudyInstanceUID, "");
            var rtRefStudyItem = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, "1.2.840.10008.3.1.2.3.1" /* Detached Study Mgmt */ },
                { DicomTag.ReferencedSOPInstanceUID, studyUid }
            };
            rtRefStudyItem.Add(rtRefSeriesSeq);

            var rtRefStudySeq = new DicomSequence(DicomTag.RTReferencedStudySequence);
            rtRefStudySeq.Items.Add(rtRefStudyItem);

            var refFrameItem = new DicomDataset
            {
                { DicomTag.FrameOfReferenceUID, frameUid }
            };
            refFrameItem.Add(rtRefStudySeq);

            var refFrameSeq = new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence);
            refFrameSeq.Items.Add(refFrameItem);
            ds.AddOrUpdate(refFrameSeq);

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
        /// Formats a double as a DICOM DS (Decimal String) value: at most 16 characters per value.
        /// Selects the highest precision that fits within the 16-character VR limit.
        /// </summary>
        private static string FormatDicomDS(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return "0";

            // Try increasing precision (G16 is too long for some negatives) — find the highest that fits.
            for (int prec = 16; prec >= 1; prec--)
            {
                string s = d.ToString("G" + prec, System.Globalization.CultureInfo.InvariantCulture);
                if (s.Length <= 16) return s;
            }
            // Fallback: round to nearest integer
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
