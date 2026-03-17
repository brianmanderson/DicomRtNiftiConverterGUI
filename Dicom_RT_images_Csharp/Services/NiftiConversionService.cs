using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        public void ConvertImageSeriesToNifti(
            DicomSeriesGroup series,
            string outputDir,
            IProgress<string> progress,
            CancellationToken ct)
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

            string outputPath = Path.Combine(outputDir, "image.nii.gz");
            SimpleITK.WriteImage(image, outputPath);
            image.Dispose();

            progress?.Report($"  Wrote {outputPath}");
        }

        /// <summary>
        /// Converts an RT Dose file to dose.nii.gz, applying DoseGridScaling if present.
        /// </summary>
        public void ConvertDoseToNifti(
            DicomSeriesGroup doseSeries,
            string outputDir,
            IProgress<string> progress,
            CancellationToken ct)
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

            string outputPath = Path.Combine(outputDir, "dose.nii.gz");
            SimpleITK.WriteImage(doseImage, outputPath);
            doseImage.Dispose();

            progress?.Report($"  Wrote {outputPath}");
        }

        /// <summary>
        /// Converts RT Struct contours to per-ROI binary mask .nii.gz files.
        /// </summary>
        public void ConvertStructToNifti(
            DicomSeriesGroup rtStructSeries,
            DicomSeriesGroup imageSeries,
            string outputDir,
            List<RoiAssociation> associations,
            bool exportUnmatched,
            IProgress<string> progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (rtStructSeries.FilePaths.Count == 0 || imageSeries.FilePaths.Count == 0) return;

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

            string rtStructFilePath = rtStructSeries.FilePaths[0];

            // Determine which ROIs to export
            var roiNamesToExport = ResolveRoiNames(rtStructSeries.RoiNames, associations, exportUnmatched);

            // Rasterize
            var masks = _maskService.RasterizeRois(
                rtStructFilePath, referenceImage, roiNamesToExport, progress, ct);

            // Write mask files
            string masksDir = Path.Combine(outputDir, "masks");
            Directory.CreateDirectory(masksDir);

            foreach (var kvp in masks)
            {
                ct.ThrowIfCancellationRequested();
                string safeName = SanitizeFileName(kvp.Key);
                string maskPath = Path.Combine(masksDir, safeName + ".nii.gz");
                SimpleITK.WriteImage(kvp.Value, maskPath);
                kvp.Value.Dispose();
                progress?.Report($"  Wrote mask: {safeName}.nii.gz");
            }

            referenceImage.Dispose();
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
