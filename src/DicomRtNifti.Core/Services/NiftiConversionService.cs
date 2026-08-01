using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using FellowOakDicom;
using itk.simple;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Converts DICOM image series, RT Dose, and RT Struct data to NIfTI (.nii.gz) format.
    /// </summary>
    public class NiftiConversionService
    {
        private readonly RtStructMaskService _maskService;

        /// <summary>
        /// Creates a new NiftiConversionService.
        /// </summary>
        public NiftiConversionService(RtStructMaskService maskService)
        {
            _maskService = maskService;
        }

        /// <summary>
        /// Converts a CT/MR/PT image series to image.nii.gz.
        /// </summary>
        /// <param name="targetSpacing">Optional output voxel spacing in mm. If non-null, image is resampled with linear interpolation.</param>
        /// <returns>Array of [spacingX, spacingY, spacingZ] from the written image (resampled or original).</returns>
        public double[] ConvertImageSeriesToNifti(
            DicomSeriesGroup series,
            string outputDir,
            IProgress<string> progress,
            CancellationToken ct,
            double[] targetSpacing = null)
        {
            ct.ThrowIfCancellationRequested();

            // Sort files by ImagePositionPatient z-coordinate
            var sortedFiles = SortFilesBySlicePosition(series.FilePaths);

            Image image = DicomImageSeriesLoader.LoadCorrected(sortedFiles);

            if (targetSpacing != null)
            {
                var resampled = ResampleToSpacing(image, targetSpacing, InterpolatorEnum.sitkLinear);
                image.Dispose();
                image = resampled;
                progress?.Report($"  Resampled image to {targetSpacing[0]}x{targetSpacing[1]}x{targetSpacing[2]} mm");
            }

            var spacing = image.GetSpacing();
            double[] result = new double[] { spacing[0], spacing[1], spacing[2] };

            string outputPath = Path.Combine(outputDir, "image.nii.gz");
            SimpleITK.WriteImage(image, outputPath);
            image.Dispose();

            progress?.Report($"  Wrote {outputPath}");
            return result;
        }

        /// <summary>
        /// Reads spacing from an image series without writing any output.
        /// </summary>
        public double[] GetImageSpacing(DicomSeriesGroup series)
        {
            var sortedFiles = SortFilesBySlicePosition(series.FilePaths);
            Image image = DicomImageSeriesLoader.LoadCorrected(sortedFiles);

            var spacing = image.GetSpacing();
            double[] result = new double[] { spacing[0], spacing[1], spacing[2] };
            image.Dispose();
            return result;
        }

        /// <summary>
        /// Converts an RT Dose file to <paramref name="outputDir"/>/doses/&lt;safe-series-description&gt;.nii.gz,
        /// applying DoseGridScaling if present. The series description from the
        /// RT-DOSE file is sanitized for filesystem use; an empty description
        /// falls back to "dose".
        ///
        /// When a study carries several doses that share a description — plans exported from the
        /// same TPS commonly all read "Eclipse Doses" — a numeric suffix keeps them from
        /// overwriting each other, provided the caller passes <paramref name="reservedNames"/>.
        /// </summary>
        /// <param name="reservedNames">
        /// Names already claimed during *this* export of *this* series, so repeated descriptions
        /// get suffixed. Must not be reused across runs: disambiguating against files left on
        /// disk by a previous run would make re-exporting a cohort accumulate a new copy of every
        /// dose each time instead of refreshing it in place. Null disables suffixing entirely.
        /// </param>
        /// <returns>The path written, or null when the dose series had no files.</returns>
        public string ConvertDoseToNifti(
            DicomSeriesGroup doseSeries,
            string outputDir,
            IProgress<string> progress,
            CancellationToken ct,
            double[] targetSpacing = null,
            HashSet<string> reservedNames = null)
        {
            ct.ThrowIfCancellationRequested();

            if (doseSeries.FilePaths.Count == 0) return null;

            string doseFilePath = doseSeries.FilePaths[0];
            Image doseImage = SimpleITK.ReadImage(doseFilePath);

            // Check for DoseGridScaling
            try
            {
                var dcmFile = DicomFile.Open(doseFilePath);
                var ds = dcmFile.Dataset;
                if (ds.Contains(DicomTag.DoseGridScaling))
                {
                    double scaling = ds.GetSingleValue<double>(DicomTag.DoseGridScaling);
                    if (Math.Abs(scaling) > 1e-10 && Math.Abs(scaling - 1.0) > 1e-10)
                    {
                        doseImage = SimpleITK.Cast(doseImage, PixelIDValueEnum.sitkFloat64);
                        doseImage = SimpleITK.Multiply(doseImage, scaling);
                    }
                }
            }
            catch (Exception)
            {
                // Proceed without scaling if tag read fails
            }

            if (targetSpacing != null)
            {
                var resampled = ResampleToSpacing(doseImage, targetSpacing, InterpolatorEnum.sitkLinear);
                doseImage.Dispose();
                doseImage = resampled;
                progress?.Report($"  Resampled dose to {targetSpacing[0]}x{targetSpacing[1]}x{targetSpacing[2]} mm");
            }

            string dosesDir = Path.Combine(outputDir, "doses");
            Directory.CreateDirectory(dosesDir);

            string baseName = string.IsNullOrWhiteSpace(doseSeries.SeriesDescription)
                ? "dose"
                : doseSeries.SeriesDescription.Trim();
            string safeName = SanitizeFileName(baseName);

            // Disambiguate against names claimed earlier in this same run — never against what is
            // already on disk, so a re-export overwrites its own previous output.
            if (reservedNames != null)
            {
                string candidate = safeName;
                for (int suffix = 2; !reservedNames.Add(candidate); suffix++)
                    candidate = $"{safeName}_{suffix}";
                safeName = candidate;
            }

            string outputPath = Path.Combine(dosesDir, safeName + ".nii.gz");

            SimpleITK.WriteImage(doseImage, outputPath);
            doseImage.Dispose();

            progress?.Report($"  Wrote {outputPath}");
            return outputPath;
        }

        /// <summary>
        /// Converts RT Struct contours to per-ROI binary mask .nii.gz files.
        /// </summary>
        /// <returns>
        /// Dictionary mapping ROI output name to mask volume (voxel count * voxel volume).
        /// Returns null if input is empty. Also returns spacing via out parameter.
        /// </returns>
        public Dictionary<string, double> ConvertStructToNifti(
            DicomSeriesGroup rtStructSeries,
            DicomSeriesGroup imageSeries,
            string outputDir,
            List<RoiAssociation> associations,
            bool exportUnmatched,
            bool flatOutput,
            IProgress<string> progress,
            CancellationToken ct,
            double[] targetSpacing = null)
        {
            ct.ThrowIfCancellationRequested();

            if (rtStructSeries.FilePaths.Count == 0 || imageSeries.FilePaths.Count == 0) return null;

            // Load reference image for geometry
            var sortedFiles = SortFilesBySlicePosition(imageSeries.FilePaths);
            Image referenceImage = DicomImageSeriesLoader.LoadCorrected(sortedFiles);
            // Per-slice ImagePositionPatient[2] (mm) for the RTSTRUCT-to-slice
            // mapping. Required for correct rasterization on non-uniform-Z
            // CT acquisitions (mixed 3 mm / 6 mm slice gaps, e.g. NSCLC-Radiomics
            // LUNG1-014): ITK's TransformPhysicalPointToContinuousIndex assumes
            // uniform spacing and shifts every contour plane by 1-2 slices in
            // the non-uniform region, missing 11-14 of the RTSTRUCT-stated
            // contour z-positions. The mask service does a nearest-slice
            // lookup against this array instead.
            double[] sliceZsMm = DicomImageSeriesLoader.ReadPerSliceZ(sortedFiles);

            var spacing = referenceImage.GetSpacing();
            double voxelVolume = spacing[0] * spacing[1] * spacing[2];

            // If resampling, the per-mask voxel volume is computed from the target grid
            double resampledVoxelVolume = targetSpacing != null
                ? targetSpacing[0] * targetSpacing[1] * targetSpacing[2]
                : voxelVolume;

            string rtStructFilePath = rtStructSeries.FilePaths[0];

            // Determine which ROIs to export
            var roiNamesToExport = ResolveRoiNames(rtStructSeries.RoiNames, associations, exportUnmatched);

            // Rasterize on the original reference grid (preserves contour fidelity)
            var masks = _maskService.RasterizeRois(
                rtStructFilePath, referenceImage, roiNamesToExport, progress, ct, sliceZsMm);

            // Compute volumes and write mask files (per-ROI work is independent;
            // resampling + StatisticsImageFilter + WriteImage are all thread-safe
            // across distinct Image instances, and SimpleITK.WriteImage writes to
            // distinct paths). We bound concurrency to 4 to avoid thrashing the
            // disk -- 4 parallel NIfTI streams already saturate a typical SSD,
            // and oversubscription past that hurts throughput more than it helps.
            var roiVolumes = new ConcurrentDictionary<string, double>();
            string masksDir;
            if (flatOutput)
            {
                masksDir = outputDir;
            }
            else
            {
                masksDir = Path.Combine(outputDir, "masks");
                Directory.CreateDirectory(masksDir);
            }

            var maskList = masks.ToList();

            // Resolve every output file name up front, and disambiguate the ones that collide.
            // Two ROIs whose names differ only in characters the sanitizer strips ("PTV:1",
            // "PTV*1") both resolve to PTV_1.nii.gz: one file on disk, several volumes reported
            // against it, and -- because this loop is parallel -- two threads writing the same
            // path at once. Same failure the dose path already guards against above.
            var maskFileNames = BuildUniqueMaskFileNames(maskList.Select(m => m.Key));
            foreach (var entry in maskFileNames)
            {
                if (!string.Equals(entry.Value, SanitizeFileName(entry.Key), StringComparison.Ordinal))
                {
                    progress?.Report(
                        $"  ROI '{entry.Key}' is not a valid file name on its own, and the sanitized " +
                        $"form is not unique; writing it as {entry.Value}.nii.gz.");
                }
            }

            // Output folders are never pruned, so a narrower re-export leaves the masks of the ROIs
            // it no longer covers sitting next to the ones it just wrote. Each of those files still
            // holds the ROI its name claims — that is what the naming rule above buys — but it is
            // older than the run that produced the rest of the folder, and nothing else says so.
            ReportStaleMaskFiles(masksDir, maskFileNames.Values, flatOutput, progress);

            var parallelOpts = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, maskList.Count)),
            };

            Parallel.ForEach(maskList, parallelOpts, kvp =>
            {
                Image maskToWrite = kvp.Value;
                double effectiveVoxelVolume = voxelVolume;

                if (targetSpacing != null)
                {
                    var resampledMask = ResampleToSpacing(kvp.Value, targetSpacing, InterpolatorEnum.sitkNearestNeighbor);
                    kvp.Value.Dispose();
                    maskToWrite = resampledMask;
                    effectiveVoxelVolume = resampledVoxelVolume;
                }

                // Per-iteration stats filter avoids cross-thread state on the
                // C++ filter object (StatisticsImageFilter caches a result
                // internally between Execute() and Get*()).
                var stats = new StatisticsImageFilter();
                stats.Execute(maskToWrite);
                double voxelCount = stats.GetSum(); // binary mask: sum == count of 1-voxels
                roiVolumes[kvp.Key] = voxelCount * effectiveVoxelVolume / 1000; // convert to cc

                string safeName = maskFileNames[kvp.Key];
                string maskPath = Path.Combine(masksDir, safeName + ".nii.gz");
                SimpleITK.WriteImage(maskToWrite, maskPath);
                maskToWrite.Dispose();
                progress?.Report($"  Wrote mask: {safeName}.nii.gz");
            });

            referenceImage.Dispose();
            return new Dictionary<string, double>(roiVolumes);
        }

        /// <summary>
        /// Computes ROI volumes without writing any mask files to disk.
        /// Returns a dictionary mapping ROI output name -> volume in cc.
        /// </summary>
        public Dictionary<string, double> ComputeStructVolumes(
            DicomSeriesGroup rtStructSeries,
            DicomSeriesGroup imageSeries,
            List<RoiAssociation> associations,
            bool exportUnmatched,
            IProgress<string> progress,
            CancellationToken ct,
            double[] targetSpacing = null)
        {
            ct.ThrowIfCancellationRequested();

            if (rtStructSeries.FilePaths.Count == 0 || imageSeries.FilePaths.Count == 0) return null;

            // Load reference image for geometry
            var sortedFiles = SortFilesBySlicePosition(imageSeries.FilePaths);
            Image referenceImage = DicomImageSeriesLoader.LoadCorrected(sortedFiles);
            // Per-slice IPP[2] for nearest-slice rasterization on
            // non-uniform-Z CTs; see ConvertRtStructToNifti for rationale.
            double[] sliceZsMm = DicomImageSeriesLoader.ReadPerSliceZ(sortedFiles);

            var spacing = referenceImage.GetSpacing();
            double voxelVolume = spacing[0] * spacing[1] * spacing[2];

            // If resampling, the per-mask voxel volume is computed from the target grid
            double resampledVoxelVolume = targetSpacing != null
                ? targetSpacing[0] * targetSpacing[1] * targetSpacing[2]
                : voxelVolume;

            string rtStructFilePath = rtStructSeries.FilePaths[0];

            // Determine which ROIs to process
            var roiNamesToExport = ResolveRoiNames(rtStructSeries.RoiNames, associations, exportUnmatched);

            // Rasterize on the original reference grid (preserves contour fidelity)
            var masks = _maskService.RasterizeRois(
                rtStructFilePath, referenceImage, roiNamesToExport, progress, ct, sliceZsMm);

            // Compute volumes only (no file writing)
            var roiVolumes = new Dictionary<string, double>();
            foreach (var kvp in masks)
            {
                ct.ThrowIfCancellationRequested();

                Image maskForStats = kvp.Value;
                double effectiveVoxelVolume = voxelVolume;

                if (targetSpacing != null)
                {
                    var resampledMask = ResampleToSpacing(kvp.Value, targetSpacing, InterpolatorEnum.sitkNearestNeighbor);
                    kvp.Value.Dispose();
                    maskForStats = resampledMask;
                    effectiveVoxelVolume = resampledVoxelVolume;
                }

                var stats = new StatisticsImageFilter();
                stats.Execute(maskForStats);
                double voxelCount = stats.GetSum();
                roiVolumes[kvp.Key] = voxelCount * effectiveVoxelVolume / 1000; // convert to cc

                maskForStats.Dispose();
            }

            referenceImage.Dispose();
            return roiVolumes;
        }

        /// <summary>
        /// Resolves which ROI names to export based on associations.
        /// Returns a dictionary mapping output name -> DICOM ROI name.
        /// </summary>
        private Dictionary<string, string> ResolveRoiNames(
            List<string> dicomRoiNames,
            List<RoiAssociation> associations,
            bool exportUnmatched)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (associations == null || associations.Count == 0)
            {
                // No associations defined: export all ROIs with original names
                foreach (var name in dicomRoiNames)
                {
                    result[name] = name;
                }
                return result;
            }

            var matchedDicomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // For each association, find the first matching DICOM ROI name. Matching is forgiving
            // (case- and punctuation-insensitive) via RoiNameMatcher so e.g. "Spinal-Cord" covers
            // "SpinalCord"; an exact case-insensitive match still wins first.
            foreach (var assoc in associations)
            {
                foreach (var dicomName in dicomRoiNames)
                {
                    bool matched = RoiNameMatcher.Matches(dicomName, assoc.CanonicalName)
                        || assoc.Aliases.Any(alias => RoiNameMatcher.Matches(dicomName, alias));

                    if (matched)
                    {
                        result[assoc.CanonicalName] = dicomName;
                        matchedDicomNames.Add(dicomName);
                        break;
                    }
                }
            }

            // Export unmatched ROIs if setting enabled
            if (exportUnmatched)
            {
                foreach (var name in dicomRoiNames)
                {
                    if (!matchedDicomNames.Contains(name) && !result.ContainsKey(name))
                    {
                        result[name] = name;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Resamples a SimpleITK image to a uniform output voxel spacing while preserving origin and direction.
        /// Output size is computed so the image covers the same physical extent as the input.
        /// </summary>
        private static Image ResampleToSpacing(Image input, double[] targetSpacing, InterpolatorEnum interpolator)
        {
            var inputSpacing = input.GetSpacing();
            var inputSize = input.GetSize();

            var newSize = new VectorUInt32();
            for (int i = 0; i < 3; i++)
            {
                uint sz = (uint)Math.Max(1, Math.Ceiling(inputSize[i] * inputSpacing[i] / targetSpacing[i]));
                newSize.Add(sz);
            }

            var spacingVec = new VectorDouble();
            foreach (var s in targetSpacing)
                spacingVec.Add(s);

            var resample = new ResampleImageFilter();
            resample.SetOutputSpacing(spacingVec);
            resample.SetSize(newSize);
            resample.SetOutputOrigin(input.GetOrigin());
            resample.SetOutputDirection(input.GetDirection());
            resample.SetInterpolator(interpolator);
            resample.SetDefaultPixelValue(0);
            return resample.Execute(input);
        }

        /// <summary>
        /// Sorts DICOM files along the slice-normal axis derived from
        /// ImageOrientationPatient. For an axial scan the sort axis reduces to
        /// IPP.Z, but the projection IPP · (row × col) is correct for any IOP
        /// (axial / coronal / sagittal / oblique). Falls back to IPP.Z if IOP
        /// can't be read from the first file.
        /// </summary>
        private List<string> SortFilesBySlicePosition(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return new List<string>();

            double[] normal = ReadSliceNormalFromIop(filePaths[0])
                              ?? new[] { 0.0, 0.0, 1.0 };

            var filePositions = new List<Tuple<string, double>>(filePaths.Count);
            foreach (var path in filePaths)
            {
                double projection = 0;
                try
                {
                    var dcm = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                    if (dcm.Dataset.Contains(DicomTag.ImagePositionPatient))
                    {
                        var ipp = dcm.Dataset.GetValues<double>(DicomTag.ImagePositionPatient);
                        if (ipp != null && ipp.Length >= 3)
                        {
                            projection = ipp[0] * normal[0]
                                       + ipp[1] * normal[1]
                                       + ipp[2] * normal[2];
                        }
                    }
                }
                catch (Exception)
                {
                    // Projection stays 0 if the file is unreadable.
                }
                filePositions.Add(Tuple.Create(path, projection));
            }

            return filePositions.OrderBy(t => t.Item2).Select(t => t.Item1).ToList();
        }

        /// <summary>
        /// Reads ImageOrientationPatient (0020,0037) from <paramref name="path"/>
        /// and returns the slice-normal unit vector (row × col). Returns null
        /// if IOP cannot be read.
        /// </summary>
        private static double[] ReadSliceNormalFromIop(string path)
        {
            try
            {
                var dcm = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                if (!dcm.Dataset.Contains(DicomTag.ImageOrientationPatient)) return null;
                var iop = dcm.Dataset.GetValues<double>(DicomTag.ImageOrientationPatient);
                if (iop == null || iop.Length < 6) return null;

                double rx = iop[0], ry = iop[1], rz = iop[2];
                double cx = iop[3], cy = iop[4], cz = iop[5];
                double nx = ry * cz - rz * cy;
                double ny = rz * cx - rx * cz;
                double nz = rx * cy - ry * cx;
                double norm = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (norm < 1e-9) return null;
                return new[] { nx / norm, ny / norm, nz / norm };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps each ROI output name to the base name (no extension) its mask is written under.
        ///
        /// Sanitizing is many-to-one — "PTV:1", "PTV*1" and "PTV?1" all become "PTV_1" — so
        /// deriving a file name from an ROI name by sanitizing alone is not safe.
        ///
        /// <b>The mapping is a pure function of the single ROI name.</b> An ROI whose name is
        /// already a valid file name keeps it verbatim; one that had to be sanitized is written as
        /// <c>&lt;sanitized&gt;_&lt;8 hex of SHA256 of the exact ROI name&gt;</c>. Nothing depends
        /// on which other ROIs were selected.
        ///
        /// That is the fix for a worse failure than the collision it replaced. Suffixes used to be
        /// handed out by rank in an ordinal sort of the *selected* set, and output folders are
        /// never pruned: exporting {"GTV:1", "GTV*1"} wrote GTV_1 = the second ROI and GTV_1_2 =
        /// the first, and re-exporting only "GTV:1" wrote GTV_1 = that ROI while the stale GTV_1_2
        /// stayed on disk. Run 1's manifest then named a file holding a different ROI's mask —
        /// silently, and indistinguishably from a correct manifest. Under a name-local rule a
        /// stale file left by a wider selection still holds the ROI its name claims, so an old
        /// manifest is at worst pointing at an old copy, never at the wrong structure.
        ///
        /// The only set-dependent case left is two ROI names that are both already valid file
        /// names and differ only in case ("PTV_1" / "ptv_1") — one file on Windows and macOS.
        /// Neither can keep the bare name, so both take the hashed form; that is symmetric, so
        /// there is still no "winner" whose name changes when the other is deselected.
        /// </summary>
        /// <param name="roiNames">ROI output names, as returned by the conversion entry points.</param>
        /// <returns>ROI output name -> mask file base name, without the ".nii.gz" suffix.</returns>
        public static Dictionary<string, string> BuildUniqueMaskFileNames(IEnumerable<string> roiNames)
        {
            var result = new Dictionary<string, string>();
            if (roiNames == null)
                return result;

            var preferred = new Dictionary<string, string>();
            foreach (var name in roiNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (name == null || preferred.ContainsKey(name))
                    continue;
                preferred[name] = PreferredMaskFileName(name);
            }

            // Case-insensitive: the collisions this exists to prevent are collisions on disk, and
            // "PTV_1" and "ptv_1" are one file on Windows and macOS.
            var claims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in preferred)
            {
                claims.TryGetValue(entry.Value, out int count);
                claims[entry.Value] = count + 1;
            }

            foreach (var entry in preferred)
            {
                result[entry.Key] = claims[entry.Value] > 1
                    ? HashedMaskFileName(entry.Key)
                    : entry.Value;
            }

            return result;
        }

        /// <summary>
        /// Names the mask files already in <paramref name="masksDir"/> that this run is not going
        /// to write. Reported, not deleted: the folder is the caller's, and a mask left by an
        /// earlier, wider selection is still valid data — it is only its currency that is in
        /// question. In flat mode the same folder legitimately holds image/dose volumes, so those
        /// are excluded rather than announced on every forward run.
        /// </summary>
        private static void ReportStaleMaskFiles(
            string masksDir,
            IEnumerable<string> writtenBaseNames,
            bool flatOutput,
            IProgress<string> progress)
        {
            if (progress == null || string.IsNullOrEmpty(masksDir) || !Directory.Exists(masksDir))
                return;

            var written = new HashSet<string>(writtenBaseNames, StringComparer.OrdinalIgnoreCase);

            var stale = new List<string>();
            foreach (var path in NiftiFileNaming.EnumerateNiftiFiles(masksDir))
            {
                string baseName = NiftiFileNaming.StripNiftiExtension(Path.GetFileName(path));
                if (written.Contains(baseName))
                    continue;
                if (flatOutput && IsNonMaskVolumeName(baseName))
                    continue;
                stale.Add(Path.GetFileName(path));
            }

            if (stale.Count == 0)
                return;

            stale.Sort(StringComparer.OrdinalIgnoreCase);
            progress.Report(
                $"  {stale.Count} pre-existing mask file(s) in {masksDir} were not written by this " +
                $"export and are left untouched: {string.Join(", ", stale)}. They are from an " +
                "earlier run with a different ROI selection — delete them if a manifest of this " +
                "run is meant to describe the whole folder.");
        }

        /// <summary>The volumes a flat forward export writes alongside the masks.</summary>
        private static bool IsNonMaskVolumeName(string baseName)
        {
            return string.Equals(baseName, "image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(baseName, "dose", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The file name an ROI takes when nothing else contests it: its own name when that is
        /// already a valid file name, otherwise the hashed form. Two distinct ROI names can only
        /// produce the same unsanitized answer if they are the same string, so this is injective
        /// except for the case-only clash <see cref="BuildUniqueMaskFileNames"/> resolves.
        /// </summary>
        private static string PreferredMaskFileName(string roiName)
        {
            string safe = SanitizeFileName(roiName);
            return string.Equals(safe, roiName, StringComparison.Ordinal)
                ? safe
                : HashedMaskFileName(roiName);
        }

        /// <summary>
        /// Sanitized name plus a short digest of the exact ROI name — the disambiguator that does
        /// not depend on what else was selected.
        /// </summary>
        private static string HashedMaskFileName(string roiName)
        {
            return SanitizeFileName(roiName) + "_" + ShortRoiNameHash(roiName);
        }

        /// <summary>
        /// First 4 bytes of SHA256(ROI name) as lowercase hex. Long enough that a clash between
        /// two ROI names in one structure set is not a practical concern, short enough to leave
        /// the file name readable.
        /// </summary>
        internal static string ShortRoiNameHash(string roiName)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(roiName ?? ""));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Removes invalid filename characters (Windows-safe; see <see cref="WindowsPathSanitizer"/>).
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            return WindowsPathSanitizer.SanitizeName(name);
        }
    }
}
