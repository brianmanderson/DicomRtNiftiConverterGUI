using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dicom_RT_images_Csharp.Models;
using FellowOakDicom;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Recursively scans a directory tree for DICOM files and groups them into a
    /// Patient -> Study -> Series hierarchy.
    /// </summary>
    public class DicomScannerService
    {
        /// <summary>
        /// Scans the given root folder for DICOM files and returns grouped patient data.
        /// </summary>
        public async Task<List<DicomPatientGroup>> ScanFolderAsync(
            string rootFolder,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            // Collect all candidate file paths
            progress?.Report("Enumerating files...");
            var allFiles = Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories).ToList();
            int totalFiles = allFiles.Count;
            int processedCount = 0;

            // Thread-safe dictionaries for grouping
            var patients = new ConcurrentDictionary<string, DicomPatientGroup>();
            var studyLookup = new ConcurrentDictionary<string, DicomStudyGroup>();
            var seriesLookup = new ConcurrentDictionary<string, DicomSeriesGroup>();

            // Process files in parallel with bounded concurrency
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            var tasks = allFiles.Select(async filePath =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessFileAsync(filePath, patients, studyLookup, seriesLookup).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Not a valid DICOM file — skip silently
                }
                finally
                {
                    semaphore.Release();
                    int count = Interlocked.Increment(ref processedCount);
                    if (count % 50 == 0 || count == totalFiles)
                    {
                        progress?.Report($"Scanned {count}/{totalFiles} files...");
                    }
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Link RTSTRUCT and RTDOSE to their parent image series
            progress?.Report("Linking RT data to image series...");
            LinkRtDataToImageSeries(patients.Values.ToList(), seriesLookup);

            progress?.Report("Scan complete.");
            return patients.Values.OrderBy(p => p.PatientID).ToList();
        }

        /// <summary>
        /// Opens a single file as DICOM and adds it to the grouping dictionaries.
        /// </summary>
        private async Task ProcessFileAsync(
            string filePath,
            ConcurrentDictionary<string, DicomPatientGroup> patients,
            ConcurrentDictionary<string, DicomStudyGroup> studyLookup,
            ConcurrentDictionary<string, DicomSeriesGroup> seriesLookup)
        {
            DicomFile dcmFile;
            try
            {
                dcmFile = await DicomFile.OpenAsync(filePath, FileReadOption.SkipLargeTags).ConfigureAwait(false);
            }
            catch
            {
                return; // Not a DICOM file
            }

            var ds = dcmFile.Dataset;
            if (ds == null) return;

            string patientId = GetStringTag(ds, DicomTag.PatientID, "Unknown");
            string patientName = GetStringTag(ds, DicomTag.PatientName, "");
            string modality = GetStringTag(ds, DicomTag.Modality, "");
            string studyUid = GetStringTag(ds, DicomTag.StudyInstanceUID, "");
            string seriesUid = GetStringTag(ds, DicomTag.SeriesInstanceUID, "");
            string seriesDesc = GetStringTag(ds, DicomTag.SeriesDescription, "");
            string seriesDate = GetStringTag(ds, DicomTag.SeriesDate, "");
            string frameOfRef = GetStringTag(ds, DicomTag.FrameOfReferenceUID, "");

            if (string.IsNullOrEmpty(studyUid) || string.IsNullOrEmpty(seriesUid))
                return;

            // Get or create patient
            var patient = patients.GetOrAdd(patientId, id => new DicomPatientGroup
            {
                PatientID = id,
                PatientName = patientName
            });
            // Update name if previously empty
            if (string.IsNullOrEmpty(patient.PatientName) && !string.IsNullOrEmpty(patientName))
            {
                patient.PatientName = patientName;
            }

            // Get or create study
            var study = studyLookup.GetOrAdd(studyUid, uid =>
            {
                var s = new DicomStudyGroup
                {
                    StudyInstanceUID = uid,
                    StudyDescription = GetStringTag(ds, DicomTag.StudyDescription, ""),
                    StudyDate = GetStringTag(ds, DicomTag.StudyDate, "")
                };
                lock (patient.Studies)
                {
                    if (!patient.Studies.Any(st => st.StudyInstanceUID == uid))
                    {
                        patient.Studies.Add(s);
                    }
                }
                return s;
            });

            // Get or create series
            var series = seriesLookup.GetOrAdd(seriesUid, uid =>
            {
                var sg = new DicomSeriesGroup
                {
                    SeriesInstanceUID = uid,
                    SeriesDescription = seriesDesc,
                    Modality = modality,
                    SeriesDate = seriesDate,
                    FrameOfReferenceUID = frameOfRef
                };
                lock (study.Series)
                {
                    if (!study.Series.Any(sr => sr.SeriesInstanceUID == uid))
                    {
                        study.Series.Add(sg);
                    }
                }
                return sg;
            });

            // Add file path
            lock (series.FilePaths)
            {
                series.FilePaths.Add(filePath);
            }

            // Parse RTSTRUCT-specific data
            if (modality == "RTSTRUCT")
            {
                ParseRtStructInfo(ds, series);
            }

            // Parse RTDOSE/RTSTRUCT referenced series UID
            if (modality == "RTSTRUCT" || modality == "RTDOSE")
            {
                string refSeriesUid = ExtractReferencedSeriesUID(ds, modality);
                if (!string.IsNullOrEmpty(refSeriesUid))
                {
                    series.ReferencedSeriesUID = refSeriesUid;
                }
            }
        }

        /// <summary>
        /// Parses ROI names from StructureSetROISequence in an RTSTRUCT dataset.
        /// </summary>
        private void ParseRtStructInfo(DicomDataset ds, DicomSeriesGroup series)
        {
            try
            {
                if (!ds.Contains(DicomTag.StructureSetROISequence)) return;

                var roiSeq = ds.GetSequence(DicomTag.StructureSetROISequence);
                lock (series.RoiNames)
                {
                    foreach (var item in roiSeq)
                    {
                        string roiName = GetStringTag(item, DicomTag.ROIName, "");
                        if (!string.IsNullOrEmpty(roiName) && !series.RoiNames.Contains(roiName))
                        {
                            series.RoiNames.Add(roiName);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Skip if sequence parsing fails
            }
        }

        /// <summary>
        /// Extracts the referenced image SeriesInstanceUID from RTSTRUCT or RTDOSE datasets.
        /// </summary>
        private string ExtractReferencedSeriesUID(DicomDataset ds, string modality)
        {
            try
            {
                if (modality == "RTSTRUCT")
                {
                    // Navigate: ReferencedFrameOfReferenceSequence > RTReferencedStudySequence > RTReferencedSeriesSequence > SeriesInstanceUID
                    if (ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence))
                    {
                        var refFrameSeq = ds.GetSequence(DicomTag.ReferencedFrameOfReferenceSequence);
                        foreach (var frameItem in refFrameSeq)
                        {
                            if (frameItem.Contains(DicomTag.RTReferencedStudySequence))
                            {
                                var refStudySeq = frameItem.GetSequence(DicomTag.RTReferencedStudySequence);
                                foreach (var studyItem in refStudySeq)
                                {
                                    if (studyItem.Contains(DicomTag.RTReferencedSeriesSequence))
                                    {
                                        var refSeriesSeq = studyItem.GetSequence(DicomTag.RTReferencedSeriesSequence);
                                        foreach (var seriesItem in refSeriesSeq)
                                        {
                                            string uid = GetStringTag(seriesItem, DicomTag.SeriesInstanceUID, "");
                                            if (!string.IsNullOrEmpty(uid))
                                                return uid;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if (modality == "RTDOSE")
                {
                    // RTDOSE references via ReferencedRTPlanSequence or ReferencedStructureSetSequence
                    // Fallback: use FrameOfReferenceUID matching
                    if (ds.Contains(DicomTag.ReferencedStructureSetSequence))
                    {
                        var refSeq = ds.GetSequence(DicomTag.ReferencedStructureSetSequence);
                        foreach (var item in refSeq)
                        {
                            string uid = GetStringTag(item, DicomTag.ReferencedSOPInstanceUID, "");
                            if (!string.IsNullOrEmpty(uid))
                                return uid; // This is the RT Struct SOP UID, not series UID
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore parsing errors
            }
            return "";
        }

        /// <summary>
        /// Links RTSTRUCT and RTDOSE series to their parent image series.
        /// </summary>
        private void LinkRtDataToImageSeries(
            List<DicomPatientGroup> patientGroups,
            ConcurrentDictionary<string, DicomSeriesGroup> seriesLookup)
        {
            foreach (var patient in patientGroups)
            {
                foreach (var study in patient.Studies)
                {
                    var imageSeries = study.Series
                        .Where(s => s.Modality == "CT" || s.Modality == "MR" || s.Modality == "PT")
                        .ToList();
                    var rtStructSeries = study.Series.Where(s => s.Modality == "RTSTRUCT").ToList();
                    var rtDoseSeries = study.Series.Where(s => s.Modality == "RTDOSE").ToList();

                    foreach (var rtStruct in rtStructSeries)
                    {
                        // Try to match by referenced SeriesInstanceUID
                        DicomSeriesGroup matchedImage = null;
                        if (!string.IsNullOrEmpty(rtStruct.ReferencedSeriesUID))
                        {
                            matchedImage = imageSeries.FirstOrDefault(
                                img => img.SeriesInstanceUID == rtStruct.ReferencedSeriesUID);
                        }

                        // Fallback: match by FrameOfReferenceUID
                        if (matchedImage == null && !string.IsNullOrEmpty(rtStruct.FrameOfReferenceUID))
                        {
                            matchedImage = imageSeries.FirstOrDefault(
                                img => img.FrameOfReferenceUID == rtStruct.FrameOfReferenceUID);
                        }

                        // Fallback: match first image series in same study
                        if (matchedImage == null && imageSeries.Count > 0)
                        {
                            matchedImage = imageSeries[0];
                        }

                        if (matchedImage != null)
                        {
                            matchedImage.LinkedRtStruct = rtStruct;
                        }
                    }

                    foreach (var rtDose in rtDoseSeries)
                    {
                        // Match by FrameOfReferenceUID
                        DicomSeriesGroup matchedImage = null;
                        if (!string.IsNullOrEmpty(rtDose.FrameOfReferenceUID))
                        {
                            matchedImage = imageSeries.FirstOrDefault(
                                img => img.FrameOfReferenceUID == rtDose.FrameOfReferenceUID);
                        }

                        // Fallback: first image series in same study
                        if (matchedImage == null && imageSeries.Count > 0)
                        {
                            matchedImage = imageSeries[0];
                        }

                        if (matchedImage != null)
                        {
                            matchedImage.LinkedRtDose = rtDose;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Safely reads a string DICOM tag with a fallback default value.
        /// </summary>
        private static string GetStringTag(DicomDataset ds, DicomTag tag, string defaultValue)
        {
            try
            {
                if (ds.Contains(tag))
                {
                    return ds.GetSingleValueOrDefault(tag, defaultValue);
                }
            }
            catch (Exception)
            {
                // Ignore
            }
            return defaultValue;
        }
    }
}
