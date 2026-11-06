using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
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
        /// Files read at once. Each read is synchronous file I/O (see ReadHeader), so this is also
        /// the depth of the disk queue. Twice the core count was within a few percent of the best of
        /// 6 to 64 on a local SSD, warm and with the page cache purged; more did not help.
        /// </summary>
        private static readonly int MaxConcurrentReads = Environment.ProcessorCount * 2;

        /// <summary>
        /// Scans the given root folder for DICOM files and returns grouped patient data plus
        /// a count of files that errored during open (so callers can surface silent failures
        /// like PathTooLongException on long Windows paths instead of reporting "0 patients").
        /// </summary>
        /// <param name="progress">The human log.</param>
        /// <param name="scanProgress">
        /// Optional structured counts for a progress bar (see <see cref="ScanProgress"/>); the log
        /// is the same with or without it.
        /// </param>
        public async Task<DicomScanResult> ScanFolderAsync(
            string rootFolder,
            IProgress<string> progress,
            CancellationToken cancellationToken,
            IProgress<ScanProgress> scanProgress = null)
        {
            progress?.Report("Enumerating files...");
            int foundCount = 0;
            // -1 until the directory walk has finished; then the number of files it found.
            int totalFiles = -1;
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

            // Files are read in parallel but applied to the hierarchy one at a time, in the order the
            // directory walk found them. Several attributes come from a group's first file (series
            // description and date, study description, the patient's name, in-plane spacing), and
            // a study or series joins the hierarchy where its first file puts it; applying in walk
            // order keeps all of that exactly as the sequential scan had it, for any input, and
            // leaves the grouping itself single-threaded. A read that finishes early waits in
            // readyToApply, as a few strings, until every file before it has been applied.
            var readyToApply = new Dictionary<int, ScannedFile>();
            int nextToApply = 0;
            object applyLock = new object();

            // The directory walk hands each path to the readers as it finds it, so reading starts
            // with the first file rather than after the whole tree has been listed. The channel is
            // unbounded because the walk outpaces reading by orders of magnitude and finishing it
            // early is what gives the progress a total; it never holds more than the complete path
            // list the scan used to build before reading anything.
            var paths = Channel.CreateUnbounded<(int Index, string Path)>(new UnboundedChannelOptions { SingleWriter = true });
            using (var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                ExceptionDispatchInfo walkError = null;
                var walk = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        // SearchOption.AllDirectories, as before: hidden and system files are
                        // included, and an inaccessible subdirectory ends the scan with its exception.
                        foreach (var path in Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories))
                        {
                            if (abort.IsCancellationRequested) return;
                            int index = Interlocked.Increment(ref foundCount) - 1;
                            paths.Writer.TryWrite((index, path));
                        }
                        Volatile.Write(ref totalFiles, foundCount);
                    }
                    catch (Exception ex)
                    {
                        // Stop the readers; the scan rethrows this exception as it always has.
                        walkError = ExceptionDispatchInfo.Capture(ex);
                        abort.Cancel();
                    }
                    finally
                    {
                        paths.Writer.Complete();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                try
                {
                    await Parallel.ForEachAsync(
                        paths.Reader.ReadAllAsync(),
                        new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentReads, CancellationToken = abort.Token },
                        (item, _) =>
                        {
                            string filePath = item.Path;
                            var scanned = new ScannedFile { Path = filePath };
                            try
                            {
                                scanned.Header = ReadHeader(filePath);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                // Anything that escapes ReadHeader is an open/parse error worth
                                // surfacing — PathTooLongException, UnauthorizedAccessException, IOException,
                                // DicomDataException on truncated files, etc. Genuinely "not a DICOM file"
                                // is filtered inside ReadHeader and never reaches here.
                                scanned.Error = $"{filePath}: {ex.GetType().Name}: {ex.Message}";
                            }
                            finally
                            {
                                lock (applyLock)
                                {
                                    readyToApply.Add(item.Index, scanned);
                                    while (readyToApply.Remove(nextToApply, out var next))
                                    {
                                        nextToApply++;
                                        if (next.Error != null)
                                        {
                                            skippedErrorCount++;
                                            if (errorSamples.Count < MaxErrorSamples)
                                                errorSamples.Enqueue(next.Error);
                                        }
                                        else if (next.Header != null)
                                        {
                                            AddToHierarchy(next.Path, next.Header, patients, studyLookup, seriesLookup, modalityCounts);
                                        }
                                    }
                                }

                                int count = Interlocked.Increment(ref processedCount);
                                if (count % 50 == 0)
                                {
                                    int total = Volatile.Read(ref totalFiles);
                                    int found = Volatile.Read(ref foundCount);
                                    progress?.Report(total >= 0
                                        ? $"Scanned {count}/{total} files..."
                                        : $"Scanned {count} files, {found} found so far...");
                                    scanProgress?.Report(new ScanProgress(count, total >= 0 ? total : found, total >= 0));
                                }
                            }
                            return default;
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (walkError != null && !cancellationToken.IsCancellationRequested)
                {
                    // The walk failed and stopped the readers; its exception is the one to report.
                }
                finally
                {
                    await walk.ConfigureAwait(false);
                }

                walkError?.Throw();
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (totalFiles > 0 && totalFiles % 50 != 0)
            {
                progress?.Report($"Scanned {totalFiles}/{totalFiles} files...");
            }
            // Always, even where the log already said it: the one event sent after every read, so
            // the last a progress bar sees is the final count.
            scanProgress?.Report(new ScanProgress(processedCount, totalFiles, walkFinished: true));

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

        /// <summary>One walked file: its header values, or the error that stopped its read.</summary>
        private sealed class ScannedFile
        {
            public string Path;
            public FileHeader Header;
            public string Error;
        }

        /// <summary>
        /// Everything AddToHierarchy needs from one file, read while its dataset is open so the
        /// dataset itself is not kept while the file waits for its turn.
        /// </summary>
        private sealed class FileHeader
        {
            public string PatientId;
            public string PatientName;
            public string Modality;
            public string StudyUid;
            public string SeriesUid;
            public string SeriesDescription;
            public string SeriesDate;
            public string FrameOfReferenceUid;
            public string StudyDescription;
            public string StudyDate;
            public bool Positioned;
            public double SliceZ;
            /// <summary>Set for an image slice with no usable position, which is recorded, not counted.</summary>
            public string UnpositionedSlice;
            public double[] PixelSpacing;
            public double? SliceThickness;
            /// <summary>Non-empty ROI names in file order (RTSTRUCT only).</summary>
            public List<string> RoiNames;
            public string ReferencedSeriesUid;
            // Read for the header-tier analysis; nothing the hierarchy is grouped or linked by.
            public double[] ImageOrientationPatient;
            public int? Rows;
            public int? Columns;
            public double? RescaleSlope;
            public double? RescaleIntercept;
            public int? NumberOfFrames;
            /// <summary>Every frame of reference a structure set names (RTSTRUCT only).</summary>
            public List<string> FrameOfReferenceUids;
        }

        /// <summary>
        /// Opens a single file as DICOM and reads the values the hierarchy is built from. Returns
        /// null for a file that is genuinely not DICOM, or that has no study or series UID; all
        /// other failures (path too long, access denied, truncated read, etc.) propagate so the
        /// caller can count and report them rather than silently producing "0 patients".
        /// </summary>
        private static FileHeader ReadHeader(string filePath)
        {
            DicomFile dcmFile;
            try
            {
                // Synchronous on purpose. fo-dicom's OpenAsync reads the same synchronous FileStream
                // and returns an already-completed task, so the one-task-per-file scan that used it
                // ran every file inline on a single thread; the parallelism is in ScanFolderAsync.
                // The two parse identically and wrap failures the same way.
                dcmFile = DicomFile.Open(filePath, DicomEncoding.Default, StopAtBulkSequences, FileReadOption.SkipLargeTags);
                if (dcmFile.IsPartial && !CanUsePartialRead(GetStringTag(dcmFile.Dataset, DicomTag.Modality, "")))
                    dcmFile = DicomFile.Open(filePath, FileReadOption.SkipLargeTags);
            }
            // System-level errors that should NOT be silently swallowed — propagate to the
            // outer counter so the GUI can warn the user. PathTooLong in particular is the
            // canary for missing Windows long-path support on nested-UID datasets.
            // fo-dicom wraps every failure to open or read the file in DicomFileException, so a
            // locked, access-denied or over-long path arrives as its InnerException; rethrow
            // that, with its own type and message, ahead of the DicomException skip below.
            catch (DicomFileException ex) when (IsFileAccessFailure(ex.InnerException))
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (PathTooLongException)        { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (DirectoryNotFoundException)  { throw; }
            // Race: file vanished between enumeration and open — benign.
            catch (FileNotFoundException) { return null; }
            // Parser errors from FellowOakDicom: not a DICOM file (or corrupt). Treat as a
            // silent skip so directories that mix DICOM with .nii/.json/.txt still scan cleanly.
            catch (DicomException) { return null; }
            // Truncated reads on non-DICOM data: benign.
            catch (System.IO.EndOfStreamException) { return null; }

            var ds = dcmFile.Dataset;
            if (ds == null) return null;

            var header = new FileHeader
            {
                PatientId = GetStringTag(ds, DicomTag.PatientID, "Unknown"),
                PatientName = GetStringTag(ds, DicomTag.PatientName, ""),
                Modality = GetStringTag(ds, DicomTag.Modality, ""),
                StudyUid = GetStringTag(ds, DicomTag.StudyInstanceUID, ""),
                SeriesUid = GetStringTag(ds, DicomTag.SeriesInstanceUID, ""),
                SeriesDescription = GetStringTag(ds, DicomTag.SeriesDescription, ""),
                SeriesDate = GetStringTag(ds, DicomTag.SeriesDate, ""),
                FrameOfReferenceUid = GetStringTag(ds, DicomTag.FrameOfReferenceUID, ""),
            };
            string modality = header.Modality;
            // RTSTRUCT has no Frame of Reference module: the top-level tag is normally absent and
            // the frame lives in ReferencedFrameOfReferenceSequence / on each ROI. Reading only the
            // top-level tag left the frame-of-reference link rule dead for real structure sets.
            if (string.Equals(modality, "RTSTRUCT", StringComparison.OrdinalIgnoreCase))
            {
                header.FrameOfReferenceUids = ExtractRtStructFrameOfReferenceUids(ds);
                if (string.IsNullOrEmpty(header.FrameOfReferenceUid))
                    header.FrameOfReferenceUid = header.FrameOfReferenceUids.FirstOrDefault() ?? "";
            }

            if (string.IsNullOrEmpty(header.StudyUid) || string.IsNullOrEmpty(header.SeriesUid))
                return null;

            header.StudyDescription = GetStringTag(ds, DicomTag.StudyDescription, "");
            header.StudyDate = GetStringTag(ds, DicomTag.StudyDate, "");

            // Capture the per-slice geometry while this header is already open. Recording it here
            // is what lets SeriesGeometryProbe report slice spacing and uniformity without a second
            // pass over every file in the tree.
            string why;
            header.Positioned = SeriesGeometryProbe.TryReadSliceZ(ds, out header.SliceZ, out why);
            // An image slice with no usable position is not part of the series the converter can
            // build (the CLI's series builder skips it too), so it is recorded instead of counted.
            // A multi-frame object (Enhanced CT/MR, NM) carries its positions per frame, so it is
            // kept whole, as it always was, rather than reported as an unpositioned slice.
            if (ImageModalities.Contains(modality) && !header.Positioned && !SeriesGeometryProbe.IsMultiFrame(ds))
                header.UnpositionedSlice = SeriesGeometryProbe.DescribeUnpositionedSlice(filePath, ds, why);

            if (ds.Contains(DicomTag.PixelSpacing))
            {
                try
                {
                    var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                    if (ps != null && ps.Length >= 2)
                        header.PixelSpacing = new[] { ps[0], ps[1] };
                }
                catch { /* leave null; the probe falls back to reporting no in-plane spacing */ }
            }

            if (ds.Contains(DicomTag.SliceThickness))
            {
                try
                {
                    header.SliceThickness = ds.GetSingleValue<double>(DicomTag.SliceThickness);
                }
                catch { /* optional; only used as the single-slice fallback */ }
            }

            // The grid facts the header-tier analysis reports (orientation, field of view, rescale,
            // multi-frame). Every one sorts before ROIContourSequence and the functional group
            // sequences, so the partial read has them; a malformed value is left null.
            header.ImageOrientationPatient = ReadDoubles(ds, DicomTag.ImageOrientationPatient, 6);
            header.Rows = ReadInt(ds, DicomTag.Rows);
            header.Columns = ReadInt(ds, DicomTag.Columns);
            header.RescaleSlope = ReadDoubles(ds, DicomTag.RescaleSlope, 1)?[0];
            header.RescaleIntercept = ReadDoubles(ds, DicomTag.RescaleIntercept, 1)?[0];
            header.NumberOfFrames = ReadInt(ds, DicomTag.NumberOfFrames);

            if (modality == "RTSTRUCT")
                header.RoiNames = ReadRoiNames(ds);

            // Referenced image series of an RTSTRUCT, referenced structure set of an RTDOSE
            if (modality == "RTSTRUCT" || modality == "RTDOSE")
                header.ReferencedSeriesUid = ExtractReferencedSeriesUID(ds, modality);

            return header;
        }

        /// <summary>
        /// Adds one file to the grouping dictionaries. Runs under the scan's apply lock, one file at
        /// a time and in directory-walk order, so a group's first file is the first one walked.
        /// </summary>
        private static void AddToHierarchy(
            string filePath,
            FileHeader header,
            ConcurrentDictionary<string, DicomPatientGroup> patients,
            ConcurrentDictionary<string, DicomStudyGroup> studyLookup,
            ConcurrentDictionary<string, DicomSeriesGroup> seriesLookup,
            ConcurrentDictionary<string, ConcurrentDictionary<string, int>> modalityCounts)
        {
            string modality = header.Modality;

            // Tally this file's modality so the series modality can be reconciled to the
            // majority after the scan (see ReconcileSeriesModalities).
            if (!string.IsNullOrEmpty(modality))
            {
                modalityCounts
                    .GetOrAdd(header.SeriesUid, _ => new ConcurrentDictionary<string, int>())
                    .AddOrUpdate(modality, 1, (_, c) => c + 1);
            }

            // Get or create patient
            var patient = patients.GetOrAdd(header.PatientId, id => new DicomPatientGroup
            {
                PatientID = id,
                PatientName = header.PatientName
            });
            // Update name if previously empty
            if (string.IsNullOrEmpty(patient.PatientName) && !string.IsNullOrEmpty(header.PatientName))
            {
                patient.PatientName = header.PatientName;
            }

            // Get or create study; a new study joins the patient of its first file
            var study = studyLookup.GetOrAdd(header.StudyUid, uid =>
            {
                var s = new DicomStudyGroup
                {
                    StudyInstanceUID = uid,
                    StudyDescription = header.StudyDescription,
                    StudyDate = header.StudyDate
                };
                patient.Studies.Add(s);
                return s;
            });

            // Get or create series; a new series joins the study of its first file
            var series = seriesLookup.GetOrAdd(header.SeriesUid, uid =>
            {
                var sg = new DicomSeriesGroup
                {
                    SeriesInstanceUID = uid,
                    SeriesDescription = header.SeriesDescription,
                    Modality = modality,
                    SeriesDate = header.SeriesDate,
                    FrameOfReferenceUID = header.FrameOfReferenceUid
                };
                study.Series.Add(sg);
                return sg;
            });

            // RT objects legitimately carry no position and are kept as before; an RTDOSE's single
            // position is still captured.
            if (header.UnpositionedSlice != null)
            {
                series.UnpositionedSlices.Add(header.UnpositionedSlice);
            }
            else
            {
                series.FilePaths.Add(filePath);
                if (header.Positioned)
                    series.SlicePositions.Add(header.SliceZ);
            }

            if (series.PixelSpacing == null && header.PixelSpacing != null)
                series.PixelSpacing = header.PixelSpacing;

            if (series.SliceThickness == null && header.SliceThickness != null)
                series.SliceThickness = header.SliceThickness;

            RecordGridFacts(series, header);

            if (header.RoiNames != null)
            {
                foreach (var roiName in header.RoiNames)
                {
                    if (!series.RoiNames.Contains(roiName))
                        series.RoiNames.Add(roiName);
                }

                // RoiNames keeps one copy of a name; the analysis needs to know a structure set
                // listed it twice, which an export resolves by suffixing the later ROINumbers.
                var seenInFile = new HashSet<string>(StringComparer.Ordinal);
                foreach (var roiName in header.RoiNames)
                {
                    if (!seenInFile.Add(roiName) && !series.DuplicateRoiNames.Contains(roiName))
                        series.DuplicateRoiNames.Add(roiName);
                }
            }

            if (header.FrameOfReferenceUids != null)
            {
                foreach (var frame in header.FrameOfReferenceUids)
                {
                    if (!series.FrameOfReferenceUIDs.Contains(frame))
                        series.FrameOfReferenceUIDs.Add(frame);
                }
            }

            if (!string.IsNullOrEmpty(header.ReferencedSeriesUid))
            {
                series.ReferencedSeriesUID = header.ReferencedSeriesUid;
            }
        }

        /// <summary>Two direction cosines within this much of each other are the same orientation.</summary>
        private const double OrientationTolerance = 1e-4;

        /// <summary>
        /// The grid facts a series keeps from its first file, as <see cref="DicomSeriesGroup.PixelSpacing"/>
        /// does, plus whether a later file disagrees. Whether the files agree does not depend on
        /// which was first, so the "varies" flags are the same for any walk order even though the
        /// first file's values are not.
        /// </summary>
        private static void RecordGridFacts(DicomSeriesGroup series, FileHeader header)
        {
            if (header.ImageOrientationPatient != null)
            {
                if (series.ImageOrientationPatient == null)
                    series.ImageOrientationPatient = header.ImageOrientationPatient;
                else if (!SameOrientation(series.ImageOrientationPatient, header.ImageOrientationPatient))
                    series.OrientationVaries = true;
            }

            if (series.Rows == null && header.Rows != null)
                series.Rows = header.Rows;
            if (series.Columns == null && header.Columns != null)
                series.Columns = header.Columns;
            if (series.NumberOfFrames == null && header.NumberOfFrames != null)
                series.NumberOfFrames = header.NumberOfFrames;

            if (header.RescaleSlope != null || header.RescaleIntercept != null)
            {
                if (!series.HasRescale)
                {
                    series.RescaleSlope = header.RescaleSlope;
                    series.RescaleIntercept = header.RescaleIntercept;
                    series.HasRescale = true;
                }
                else if (series.RescaleSlope != header.RescaleSlope || series.RescaleIntercept != header.RescaleIntercept)
                {
                    series.RescaleVaries = true;
                }
            }
        }

        private static bool SameOrientation(double[] a, double[] b)
        {
            for (int i = 0; i < 6; i++)
            {
                if (Math.Abs(a[i] - b[i]) > OrientationTolerance)
                    return false;
            }
            return true;
        }

        /// <summary>The first <paramref name="count"/> values of a numeric tag, or null when absent, short or unparseable.</summary>
        private static double[] ReadDoubles(DicomDataset ds, DicomTag tag, int count)
        {
            try
            {
                if (!ds.Contains(tag)) return null;
                var values = ds.GetValues<double>(tag);
                if (values == null || values.Length < count) return null;
                for (int i = 0; i < count; i++)
                {
                    if (double.IsNaN(values[i]) || double.IsInfinity(values[i])) return null;
                }
                return values.Take(count).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int? ReadInt(DicomDataset ds, DicomTag tag)
        {
            try
            {
                if (!ds.Contains(tag)) return null;
                return ds.GetSingleValue<int>(tag);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the cause fo-dicom wrapped in a DicomFileException means the file could not be
        /// opened or read (access denied, a sharing violation, a path too long), as opposed to read
        /// and found not to be DICOM. A truncated read and a file that vanished after enumeration
        /// stay silent skips, as their unwrapped catches in ReadHeader have them.
        /// </summary>
        private static bool IsFileAccessFailure(Exception cause) =>
            cause is UnauthorizedAccessException ||
            (cause is IOException && !(cause is EndOfStreamException) && !(cause is FileNotFoundException));

        /// <summary>
        /// Stops a read at the top-level sequences that make up most of the bytes of the objects
        /// that carry them: a structure set's contour points (ROIContourSequence) and an enhanced or
        /// segmentation object's per-frame description (the functional group sequences). On the
        /// NSCLC-Radiomics cohort these were half the scan's parse time; the scanner reads nothing
        /// in them, or after them, except on the objects <see cref="CanUsePartialRead"/> sends back
        /// for a full read.
        /// </summary>
        private static readonly Func<ParseState, bool> StopAtBulkSequences = state =>
            state.SequenceDepth == 0 &&
            (state.Tag == DicomTag.ROIContourSequence ||
             state.Tag == DicomTag.SharedFunctionalGroupsSequence ||
             state.Tag == DicomTag.PerFrameFunctionalGroupsSequence);

        /// <summary>
        /// Whether a read that ended early (at <see cref="StopAtBulkSequences"/>, or at a truncation)
        /// holds every tag ReadHeader reads for this modality. Every tag it reads sorts before
        /// ROIContourSequence (3006,0039) with two exceptions, and those modalities get a full read:
        /// an image's PerFrameFunctionalGroupsSequence (5200,9230), which decides whether a slice
        /// without a position is a multi-frame object, and a dose's ReferencedStructureSetSequence
        /// (300C,0060), which may follow a dose's own ROI contours.
        /// </summary>
        private static bool CanUsePartialRead(string modality) =>
            !ImageModalities.Contains(modality) &&
            !string.Equals(modality, "RTDOSE", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Non-empty ROI names from StructureSetROISequence in an RTSTRUCT dataset, in file order.
        /// </summary>
        private static List<string> ReadRoiNames(DicomDataset ds)
        {
            var names = new List<string>();
            try
            {
                if (!ds.Contains(DicomTag.StructureSetROISequence)) return names;

                foreach (var item in ds.GetSequence(DicomTag.StructureSetROISequence))
                {
                    string roiName = GetStringTag(item, DicomTag.ROIName, "");
                    if (!string.IsNullOrEmpty(roiName))
                        names.Add(roiName);
                }
            }
            catch (Exception)
            {
                // Keep the names read before the sequence failed to parse
            }
            return names;
        }

        /// <summary>
        /// Extracts the referenced image SeriesInstanceUID from RTSTRUCT or RTDOSE datasets.
        /// Static and internal so the CLI's explicit --forward route can ask the same question
        /// of a structure set it was handed directly.
        /// </summary>
        internal static List<string> ExtractReferencedSeriesUIDs(DicomDataset ds)
        {
            var uids = new List<string>();
            try
            {
                if (!ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence))
                    return uids;
                foreach (var frameItem in ds.GetSequence(DicomTag.ReferencedFrameOfReferenceSequence))
                {
                    if (!frameItem.Contains(DicomTag.RTReferencedStudySequence)) continue;
                    foreach (var studyItem in frameItem.GetSequence(DicomTag.RTReferencedStudySequence))
                    {
                        if (!studyItem.Contains(DicomTag.RTReferencedSeriesSequence)) continue;
                        foreach (var seriesItem in studyItem.GetSequence(DicomTag.RTReferencedSeriesSequence))
                        {
                            string uid = GetStringTag(seriesItem, DicomTag.SeriesInstanceUID, "");
                            if (!string.IsNullOrEmpty(uid) && !uids.Contains(uid))
                                uids.Add(uid);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A malformed sequence yields what was read before it.
            }
            return uids;
        }

        /// <summary>
        /// Every frame of reference a structure set names: the top-level tag when present, the
        /// ReferencedFrameOfReferenceSequence items, and each ROI's ReferencedFrameOfReferenceUID,
        /// in that order, without duplicates.
        /// </summary>
        internal static List<string> ExtractRtStructFrameOfReferenceUids(DicomDataset ds)
        {
            var uids = new List<string>();
            void Add(string uid)
            {
                if (!string.IsNullOrEmpty(uid) && !uids.Contains(uid)) uids.Add(uid);
            }

            Add(GetStringTag(ds, DicomTag.FrameOfReferenceUID, ""));
            try
            {
                if (ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence))
                    foreach (var item in ds.GetSequence(DicomTag.ReferencedFrameOfReferenceSequence))
                        Add(GetStringTag(item, DicomTag.FrameOfReferenceUID, ""));
                if (ds.Contains(DicomTag.StructureSetROISequence))
                    foreach (var item in ds.GetSequence(DicomTag.StructureSetROISequence))
                        Add(GetStringTag(item, DicomTag.ReferencedFrameOfReferenceUID, ""));
            }
            catch (Exception)
            {
                // A malformed sequence yields what was read before it.
            }
            return uids;
        }

        internal static string ExtractReferencedSeriesUID(DicomDataset ds, string modality)
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
                        series.UnpositionedSlices.Sort(StringComparer.Ordinal);
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
        internal static readonly HashSet<string> ImageModalities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT", "MR", "PT", "PET", "NM" };

        /// <summary>
        /// Sets each series' Modality to the most common value seen across its files.
        /// Without this the series modality was whichever file happened to win the parallel
        /// GetOrAdd race — non-deterministic, and the cause of an MR series occasionally
        /// being badged [CT] when a stray instance carried a different Modality tag.
        /// The tally itself is kept on the series (<see cref="DicomSeriesGroup.ModalityCounts"/>),
        /// so the instances the majority outvoted can still be reported.
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
                    foreach (var count in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
                        kvp.Value.ModalityCounts[count.Key] = count.Value;
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
