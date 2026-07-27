using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using FellowOakDicom;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Recursively scans a directory tree for DICOM files and groups them into a
    /// Patient -> Study -> Series hierarchy.
    /// </summary>
    public class DicomScannerService
    {
        // Cap on how many per-file error samples we keep, so a directory full of unreadable
        // files doesn't grow the result object unboundedly. The full count is always reported.
        private const int MaxErrorSamples = 10;

        /// <summary>
        /// Scans the given root folder for DICOM files and returns grouped patient data plus
        /// a count of files that errored during open (so callers can surface silent failures
        /// like PathTooLongException on long Windows paths instead of reporting "0 patients").
        /// </summary>
        public async Task<DicomScanResult> ScanFolderAsync(
            string rootFolder,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            // Collect all candidate file paths
            progress?.Report("Enumerating files...");
            var allFiles = Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories).ToList();
            int totalFiles = allFiles.Count;
            int processedCount = 0;
            int skippedErrorCount = 0;
            var errorSamples = new ConcurrentQueue<string>();

            // Thread-safe dictionaries for grouping
            var patients = new ConcurrentDictionary<string, DicomPatientGroup>();
            var studyLookup = new ConcurrentDictionary<string, DicomStudyGroup>();
            var seriesLookup = new ConcurrentDictionary<string, DicomSeriesGroup>();

            // Per-series modality tally (SeriesUID -> Modality -> file count). DICOM
            // Modality (0008,0060) is a series-level attribute, but non-conformant exports
            // occasionally carry a stray value on one instance (a localizer, a derived/
            // secondary-capture object). We reconcile each series to the majority after the
            // scan rather than letting whichever file wins the parallel GetOrAdd race decide
            // — the latter is non-deterministic and was the cause of an MR series being
            // badged [CT].
            var modalityCounts = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();

            // Process files in parallel with bounded concurrency
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            var tasks = allFiles.Select(async filePath =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessFileAsync(filePath, patients, studyLookup, seriesLookup, modalityCounts).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Anything that escapes ProcessFileAsync is an open/parse error worth
                    // surfacing — PathTooLongException, UnauthorizedAccessException, IOException,
                    // DicomDataException on truncated files, etc. Genuinely "not a DICOM file"
                    // is filtered inside ProcessFileAsync and never reaches here.
                    Interlocked.Increment(ref skippedErrorCount);
                    if (errorSamples.Count < MaxErrorSamples)
                    {
                        errorSamples.Enqueue($"{filePath}: {ex.GetType().Name}: {ex.Message}");
                    }
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

            // Reconcile each series' modality to the majority across its files. Must run
            // before linking, since LinkRtDataToImageSeries filters series by modality.
            ReconcileSeriesModalities(seriesLookup, modalityCounts);

            // Impose a deterministic order before linking. Files are scanned in parallel, so
            // studies and series land in whatever order the races resolve — which would make
            // "the first image series in the study" (the last-resort linking rule) and the
            // singular LinkedRtStruct/LinkedRtDose references vary between runs over identical
            // input. Sorting here makes a rescan reproducible.
            SortHierarchy(patients.Values);

            // Link RTSTRUCT and RTDOSE to their parent image series
            progress?.Report("Linking RT data to image series...");
            LinkRtDataToImageSeries(patients.Values.ToList(), seriesLookup);

            progress?.Report("Scan complete.");
            return new DicomScanResult
            {
                Patients = patients.Values.OrderBy(p => p.PatientID).ToList(),
                TotalFileCount = totalFiles,
                SkippedErrorCount = skippedErrorCount,
                SkippedErrorSamples = errorSamples.ToList()
            };
        }

        /// <summary>
        /// Opens a single file as DICOM and adds it to the grouping dictionaries.
        /// Files that are genuinely not DICOM are filtered here; all other failures (path
        /// too long, access denied, truncated read, etc.) propagate so the caller can count
        /// and report them rather than silently producing "0 patients".
        /// </summary>
        private async Task ProcessFileAsync(
            string filePath,
            ConcurrentDictionary<string, DicomPatientGroup> patients,
            ConcurrentDictionary<string, DicomStudyGroup> studyLookup,
            ConcurrentDictionary<string, DicomSeriesGroup> seriesLookup,
            ConcurrentDictionary<string, ConcurrentDictionary<string, int>> modalityCounts)
        {
            DicomFile dcmFile;
            try
            {
                dcmFile = await DicomFile.OpenAsync(filePath, FileReadOption.SkipLargeTags).ConfigureAwait(false);
            }
            // System-level errors that should NOT be silently swallowed — propagate to the
            // outer counter so the GUI can warn the user. PathTooLong in particular is the
            // canary for missing Windows long-path support on nested-UID datasets.
            catch (PathTooLongException)        { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (DirectoryNotFoundException)  { throw; }
            // Race: file vanished between enumeration and open — benign.
            catch (FileNotFoundException) { return; }
            // Parser errors from FellowOakDicom: not a DICOM file (or corrupt). Treat as a
            // silent skip so directories that mix DICOM with .nii/.json/.txt still scan cleanly.
            catch (DicomException) { return; }
            // Truncated reads on non-DICOM data: benign.
            catch (System.IO.EndOfStreamException) { return; }

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

            // Tally this file's modality so the series modality can be reconciled to the
            // majority after the scan (see ReconcileSeriesModalities).
            if (!string.IsNullOrEmpty(modality))
            {
                modalityCounts
                    .GetOrAdd(seriesUid, _ => new ConcurrentDictionary<string, int>())
                    .AddOrUpdate(modality, 1, (_, c) => c + 1);
            }

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

            // Add file path, and capture the per-slice geometry while this header is already
            // open. Recording it here is what lets SeriesGeometryProbe report slice spacing and
            // uniformity without a second pass over every file in the tree.
            lock (series.FilePaths)
            {
                series.FilePaths.Add(filePath);

                if (ds.Contains(DicomTag.ImagePositionPatient))
                {
                    try
                    {
                        var ipp = ds.GetValues<double>(DicomTag.ImagePositionPatient);
                        if (ipp != null && ipp.Length >= 3)
                            series.SlicePositions.Add(ipp[2]);
                    }
                    catch { /* malformed IPP: the probe reports non-uniform rather than guessing */ }
                }

                if (series.PixelSpacing == null && ds.Contains(DicomTag.PixelSpacing))
                {
                    try
                    {
                        var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                        if (ps != null && ps.Length >= 2)
                            series.PixelSpacing = new[] { ps[0], ps[1] };
                    }
                    catch { /* leave null; the probe falls back to reporting no in-plane spacing */ }
                }

                if (series.SliceThickness == null && ds.Contains(DicomTag.SliceThickness))
                {
                    try
                    {
                        series.SliceThickness = ds.GetSingleValue<double>(DicomTag.SliceThickness);
                    }
                    catch { /* optional; only used as the single-slice fallback */ }
                }
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
                        var matchedImage = MatchRtToImageSeries(rtStruct, imageSeries, out var rule);
                        if (matchedImage != null)
                        {
                            rtStruct.LinkMatchRule = rule;

                            // Capture every structure set. A study can carry more than one (a
                            // planning-CT set plus per-fraction CBCT sets, successive
                            // re-contourings), and assigning here instead of appending silently
                            // dropped all but the last. The legacy singular reference is kept
                            // pointing at the first for back-compat with consumers not yet
                            // updated to iterate the list.
                            matchedImage.LinkedRtStructs.Add(rtStruct);
                            if (matchedImage.LinkedRtStruct == null)
                                matchedImage.LinkedRtStruct = rtStruct;
                        }
                    }

                    foreach (var rtDose in rtDoseSeries)
                    {
                        foreach (var matchedImage in MatchDoseToImageSeries(rtDose, imageSeries, out var rule))
                        {
                            rtDose.LinkMatchRule = rule;

                            // Capture every dose; a study can carry more than one (per-beam,
                            // plan-sum, multiple plans). The legacy singular reference is kept
                            // pointing at the first for back-compat with consumers not yet
                            // updated to iterate the list.
                            matchedImage.LinkedRtDoses.Add(rtDose);
                            if (matchedImage.LinkedRtDose == null)
                                matchedImage.LinkedRtDose = rtDose;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Orders studies and series deterministically after the parallel scan. Series sort by
        /// date then SeriesInstanceUID — roughly chronological, and stable when dates tie or are
        /// absent. File paths within a series are sorted too, so a series' first slice is always
        /// the same one.
        /// </summary>
        private static void SortHierarchy(IEnumerable<DicomPatientGroup> patients)
        {
            foreach (var patient in patients)
            {
                patient.Studies.Sort((a, b) =>
                    string.CompareOrdinal(a.StudyInstanceUID, b.StudyInstanceUID));

                foreach (var study in patient.Studies)
                {
                    study.Series.Sort((a, b) =>
                    {
                        int byDate = string.CompareOrdinal(a.SeriesDate ?? "", b.SeriesDate ?? "");
                        if (byDate != 0) return byDate;
                        return string.CompareOrdinal(a.SeriesInstanceUID, b.SeriesInstanceUID);
                    });

                    foreach (var series in study.Series)
                    {
                        series.FilePaths.Sort(StringComparer.Ordinal);
                        // Ascending z, so consecutive differences are the slice gaps.
                        series.SlicePositions.Sort();
                    }
                }
            }
        }

        /// <summary>
        /// Resolves the image series an RT object (RTSTRUCT or RTDOSE) belongs to, trying the
        /// available identifiers in descending order of confidence and reporting which one hit.
        ///
        /// The order matters on studies that hold several image series sharing one frame of
        /// reference — a planning CT plus CBCTs resampled onto its grid, for example. There the
        /// FrameOfReferenceUID rule cannot discriminate and would attach every structure set to
        /// whichever series happens to come first, so the referenced-SeriesInstanceUID rule has
        /// to be tried first even though it is more often absent.
        /// </summary>
        /// <returns>The matched image series, or null when the study holds no image series.</returns>
        private static DicomSeriesGroup MatchRtToImageSeries(
            DicomSeriesGroup rtSeries,
            List<DicomSeriesGroup> imageSeries,
            out RtLinkMatchRule rule)
        {
            if (!string.IsNullOrEmpty(rtSeries.ReferencedSeriesUID))
            {
                var byRefUid = imageSeries.FirstOrDefault(
                    img => img.SeriesInstanceUID == rtSeries.ReferencedSeriesUID);
                if (byRefUid != null)
                {
                    rule = RtLinkMatchRule.ReferencedSeriesUid;
                    return byRefUid;
                }
            }

            if (!string.IsNullOrEmpty(rtSeries.FrameOfReferenceUID))
            {
                var sharingFrame = imageSeries
                    .Where(img => img.FrameOfReferenceUID == rtSeries.FrameOfReferenceUID)
                    .ToList();
                if (sharingFrame.Count > 0)
                {
                    rule = RtLinkMatchRule.FrameOfReferenceUid;
                    return Fullest(sharingFrame);
                }
            }

            if (imageSeries.Count > 0)
            {
                rule = RtLinkMatchRule.LargestSeriesFallback;
                return Fullest(imageSeries);
            }

            rule = RtLinkMatchRule.None;
            return null;
        }

        /// <summary>
        /// Resolves the image series an RT-DOSE applies to. Unlike a structure set, a dose can
        /// legitimately apply to more than one.
        ///
        /// A dose names no image series. It references an RT-PLAN, which references a structure
        /// set, which references the images — but that chain is frequently unavailable (the plan
        /// is often not exported alongside the dose), leaving only the frame of reference. When
        /// several image series share one frame — a planning CT plus CBCTs rigidly registered and
        /// resampled onto its grid — that identifier cannot discriminate, and picking one is a
        /// coin flip that orphans the dose whenever the caller selects a different series.
        ///
        /// Sharing a frame of reference means sharing a patient coordinate system, so the dose is
        /// spatially valid for every series in that frame. Linking it to all of them is therefore
        /// correct rather than merely convenient, and it lets series selection — which does have
        /// the information to choose — decide what actually gets exported.
        /// </summary>
        private static List<DicomSeriesGroup> MatchDoseToImageSeries(
            DicomSeriesGroup rtDose,
            List<DicomSeriesGroup> imageSeries,
            out RtLinkMatchRule rule)
        {
            if (!string.IsNullOrEmpty(rtDose.ReferencedSeriesUID))
            {
                var byRefUid = imageSeries.FirstOrDefault(
                    img => img.SeriesInstanceUID == rtDose.ReferencedSeriesUID);
                if (byRefUid != null)
                {
                    rule = RtLinkMatchRule.ReferencedSeriesUid;
                    return new List<DicomSeriesGroup> { byRefUid };
                }
            }

            if (!string.IsNullOrEmpty(rtDose.FrameOfReferenceUID))
            {
                var sharingFrame = imageSeries
                    .Where(img => img.FrameOfReferenceUID == rtDose.FrameOfReferenceUID)
                    .ToList();
                if (sharingFrame.Count > 0)
                {
                    rule = RtLinkMatchRule.FrameOfReferenceUid;
                    return sharingFrame;
                }
            }

            if (imageSeries.Count > 0)
            {
                rule = RtLinkMatchRule.LargestSeriesFallback;
                return new List<DicomSeriesGroup> { Fullest(imageSeries) };
            }

            rule = RtLinkMatchRule.None;
            return new List<DicomSeriesGroup>();
        }

        /// <summary>
        /// Picks the image series with the most instances, breaking ties on SeriesInstanceUID.
        ///
        /// Used when only a weak identifier is available. An RT-DOSE typically carries no
        /// referenced series UID at all, so a study holding a planning CT plus CBCTs resampled
        /// onto its grid offers nothing but the shared frame of reference to choose by. The dose
        /// was computed on the planning CT, and the planning CT is the series that covers the
        /// most anatomy — so "fullest" recovers the right answer where "first" would attach the
        /// dose to whichever CBCT happened to sort first.
        /// </summary>
        private static DicomSeriesGroup Fullest(List<DicomSeriesGroup> candidates) =>
            candidates
                .OrderByDescending(s => s.FilePaths.Count)
                .ThenBy(s => s.SeriesInstanceUID, StringComparer.Ordinal)
                .First();

        /// <summary>
        /// Image modalities, used to break modality-tally ties toward a real image series.
        /// </summary>
        private static readonly HashSet<string> ImageModalities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT", "MR", "PT", "PET", "NM" };

        /// <summary>
        /// Sets each series' Modality to the most common value seen across its files.
        /// Without this the series modality was whichever file happened to win the parallel
        /// GetOrAdd race — non-deterministic, and the cause of an MR series occasionally
        /// being badged [CT] when a stray instance carried a different Modality tag.
        /// </summary>
        private static void ReconcileSeriesModalities(
            ConcurrentDictionary<string, DicomSeriesGroup> seriesLookup,
            ConcurrentDictionary<string, ConcurrentDictionary<string, int>> modalityCounts)
        {
            foreach (var kvp in seriesLookup)
            {
                if (modalityCounts.TryGetValue(kvp.Key, out var counts) && counts.Count > 0)
                {
                    kvp.Value.Modality = PickDominantModality(counts);
                }
            }
        }

        /// <summary>
        /// Picks the dominant modality: highest file count wins; ties break toward an image
        /// modality (CT/MR/PT/...), then by ordinal name so the result is deterministic.
        /// </summary>
        private static string PickDominantModality(ConcurrentDictionary<string, int> counts)
        {
            return counts
                .OrderByDescending(c => c.Value)
                .ThenByDescending(c => ImageModalities.Contains(c.Key))
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .First()
                .Key;
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
