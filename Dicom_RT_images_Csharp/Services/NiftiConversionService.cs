using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;
using itk.simple;

namespace Dicom_RT_images_Csharp.Services
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

            var fileNames = new VectorString();
            foreach (var f in sortedFiles)
            {
                fileNames.Add(f);
            }

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();

            Image image = reader.Execute();

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
            var fileNames = new VectorString();
            foreach (var f in sortedFiles)
                fileNames.Add(f);

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();
            Image image = reader.Execute();

            var spacing = image.GetSpacing();
            double[] result = new double[] { spacing[0], spacing[1], spacing[2] };
            image.Dispose();
            return result;
        }

        /// <summary>
        /// Converts an RT Dose file to dose.nii.gz, applying DoseGridScaling if present.
        /// </summary>
        public void ConvertDoseToNifti(
            DicomSeriesGroup doseSeries,
            string outputDir,
            IProgress<string> progress,
            CancellationToken ct,
            double[] targetSpacing = null)
        {
            ct.ThrowIfCancellationRequested();

            if (doseSeries.FilePaths.Count == 0) return;

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

            string outputPath = Path.Combine(outputDir, "dose.nii.gz");
            SimpleITK.WriteImage(doseImage, outputPath);
            doseImage.Dispose();

            progress?.Report($"  Wrote {outputPath}");
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
            var fileNames = new VectorString();
            foreach (var f in sortedFiles)
            {
                fileNames.Add(f);
            }

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();
            Image referenceImage = reader.Execute();

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
                rtStructFilePath, referenceImage, roiNamesToExport, progress, ct);

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

                string safeName = SanitizeFileName(kvp.Key);
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
            var fileNames = new VectorString();
            foreach (var f in sortedFiles)
            {
                fileNames.Add(f);
            }

            var reader = new ImageSeriesReader();
            reader.SetFileNames(fileNames);
            reader.MetaDataDictionaryArrayUpdateOn();
            reader.LoadPrivateTagsOn();
            Image referenceImage = reader.Execute();

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
                rtStructFilePath, referenceImage, roiNamesToExport, progress, ct);

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

            // For each association, find the first matching DICOM ROI name
            foreach (var assoc in associations)
            {
                foreach (var dicomName in dicomRoiNames)
                {
                    bool matched = string.Equals(dicomName, assoc.CanonicalName, StringComparison.OrdinalIgnoreCase);
                    if (!matched)
                    {
                        matched = assoc.Aliases.Any(alias =>
                            string.Equals(dicomName, alias, StringComparison.OrdinalIgnoreCase));
                    }

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
        /// Sorts DICOM files by the z-component of ImagePositionPatient.
        /// </summary>
        private List<string> SortFilesBySlicePosition(List<string> filePaths)
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
                        if (positions.Length >= 3)
                        {
                            zPos = positions[2];
                        }
                    }
                }
                catch (Exception)
                {
                    // Use 0 if position can't be read
                }
                filePositions.Add(Tuple.Create(path, zPos));
            }

            return filePositions.OrderBy(t => t.Item2).Select(t => t.Item1).ToList();
        }

        /// <summary>
        /// Removes invalid filename characters.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
