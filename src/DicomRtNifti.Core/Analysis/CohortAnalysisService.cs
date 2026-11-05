using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>What a cohort analysis computes, and against which association set.</summary>
    public sealed class CohortAnalysisOptions
    {
        public AnalysisTier Tier { get; set; } = AnalysisTier.Header;

        /// <summary>The scanned folder; shown in the document unless anonymized. May be null.</summary>
        public string InputRoot { get; set; }

        /// <summary>The association set ROI names are resolved against; empty for none.</summary>
        public List<RoiAssociation> Associations { get; set; } = new List<RoiAssociation>();

        /// <summary>
        /// What the document calls the association set: the CLI gives the file's name, never its
        /// path. Null when there is none.
        /// </summary>
        public string AssociationsName { get; set; }
    }

    /// <summary>
    /// The cohort analysis (<c>--cohort-analyze</c>, the web's Analysis pages): linkage, image
    /// geometry, orientation, ROI naming and flags, computed from a scan with no further I/O.
    /// Pure, so every rule is unit-tested from in-memory models; the CLI and the web print the
    /// same document because both build it here (<see cref="CohortAnalysisDocument"/>).
    ///
    /// The header tier reads nothing beyond the scan: the scanner's model and the header values it
    /// keeps (orientation, grid, rescale, frames, the modality tally). The contours tier adds a
    /// full read of every linked structure set (<see cref="RtStructContourReader"/>) and dose
    /// (<see cref="DoseGridAudit"/>) and one image header per series (<see cref="ImageGrid"/>),
    /// and adds fields to the document without changing the header tier's. The raster tier is
    /// refused until brief W4-K lands.
    /// </summary>
    public static class CohortAnalysisService
    {
        /// <param name="anon">
        /// The key to hash identifiers with, opened read-only (<see cref="ReadOnlyAnonymizationKey"/>);
        /// null for plain identifiers. Under it the analysis carries hashes only, and no path or
        /// file name: unpositioned slices are counted, never named.
        /// </param>
        /// <exception cref="NotSupportedException">The tier is not yet available.</exception>
        public static CohortAnalysis Analyze(DicomScanResult scan, CohortAnalysisOptions options, AnonymizationService anon = null)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            options = options ?? new CohortAnalysisOptions();
            if (!AnalysisTierNames.IsAvailable(options.Tier))
            {
                throw new NotSupportedException(
                    $"The {AnalysisTierNames.Of(options.Tier)} tier is not yet available; only the {AnalysisTierNames.Header} and {AnalysisTierNames.Contours} tiers are.");
            }

            var ids = new AnalysisIdentifiers(anon);
            // The contours tier reads each linked structure set once, however many series link it.
            var reads = options.Tier >= AnalysisTier.Contours
                ? new Dictionary<string, StructureSetContours>(StringComparer.Ordinal)
                : null;
            var associations = (options.Associations ?? new List<RoiAssociation>()).Where(a => a != null).ToList();
            var analysis = new CohortAnalysis
            {
                Tier = AnalysisTierNames.Of(options.Tier),
                Anonymized = ids.Anonymized,
                InputRoot = ids.Anonymized || string.IsNullOrEmpty(options.InputRoot) ? null : Path.GetFullPath(options.InputRoot),
                Associations = associations.Count == 0 ? null : options.AssociationsName,
            };

            foreach (var patient in scan.Patients)
            {
                foreach (var study in patient.Studies)
                {
                    var context = new FlagContext(analysis.Flags, ids.Patient(patient.PatientID), ids.Study(study.StudyInstanceUID));
                    var images = study.Series.Where(SeriesFlags.IsImageSeries).ToList();

                    foreach (var image in images)
                    {
                        var series = DescribeSeries(image, study, associations, ids, context);
                        if (reads != null)
                            DescribeContours(series, image, ids, context, reads);
                        analysis.Series.Add(series);
                    }

                    foreach (var obj in study.Series.Where(s => !SeriesFlags.IsImageSeries(s)))
                    {
                        if (!SeriesFlags.IsUnlinked(study, obj))
                            continue;
                        string objUid = ids.Series(obj.SeriesInstanceUID);
                        context.Raise(null, FlagCodes.LinkUnlinked, null, objUid, null, string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} ('{2}') is linked to no image series in its study ({3}), so no export includes it.",
                            obj.Modality, objUid, obj.SeriesDescription, LinkageAudit.UnlinkedReason(images.Count)));
                        if (SeriesFlags.ReferencesAbsentSeries(study, obj))
                            RaiseReferencedSeriesAbsent(context, null, null, obj, objUid, ids);
                        RaiseDuplicateNames(context, null, null, obj, objUid);
                    }

                    if (SeriesFlags.HasMultipleFrames(study, out int frames))
                    {
                        context.Raise(null, FlagCodes.LinkMultipleFrames, null, null, null, string.Format(CultureInfo.InvariantCulture,
                            "study {0}: its image series span {1} frames of reference, so they do not share one patient coordinate " +
                            "system. A structure set or dose applies to the series of its own frame; its masks do not overlay a " +
                            "series in another frame without a registration.",
                            context.StudyUid, frames));
                    }
                }
            }

            analysis.RoiNaming = RoiNamingAnalysis.Analyze(scan, associations, ids.Patient);
            foreach (var name in analysis.RoiNaming.Uncovered)
            {
                var entry = analysis.RoiNaming.Names.First(n => n.Name == name);
                analysis.Flags.Add(new AnalysisFlag
                {
                    Code = FlagCodes.RoiUnassociated,
                    Severity = FlagCodes.Get(FlagCodes.RoiUnassociated).Severity,
                    Roi = name,
                    Message = string.Format(CultureInfo.InvariantCulture,
                        "ROI name '{0}' ({1} series, {2} structure set(s)) matches no association in {3}: " +
                        "--only-associated-rois drops it; without that flag it is exported under its own name.",
                        name, entry.Series, entry.StructureSets, analysis.Associations ?? "the association set"),
                });
            }

            analysis.Linkage = LinkageAudit.Audit(scan, ids);
            analysis.Geometry = ImageGeometry.Summarize(analysis.Series);
            analysis.Summary = Summarize(scan, analysis.Flags, options.Tier);
            return analysis;
        }

        private static SeriesAnalysis DescribeSeries(
            DicomSeriesGroup image, DicomStudyGroup study, List<RoiAssociation> associations,
            AnalysisIdentifiers ids, FlagContext context)
        {
            string seriesUid = ids.Series(image.SeriesInstanceUID);
            SeriesFlags.IsOblique(image, out var orientation);
            var series = new SeriesAnalysis
            {
                PatientId = context.PatientId,
                StudyUid = context.StudyUid,
                SeriesUid = seriesUid,
                Modality = image.Modality,
                SeriesDescription = image.SeriesDescription,
                Geometry = ImageGeometry.Describe(image),
                Orientation = orientation,
                Header = new SeriesHeaderFacts
                {
                    RescaleSlope = image.RescaleVaries ? null : ImageGeometry.Round(image.RescaleSlope),
                    RescaleIntercept = image.RescaleVaries ? null : ImageGeometry.Round(image.RescaleIntercept),
                    RescaleVaries = image.RescaleVaries,
                    NumberOfFrames = image.NumberOfFrames,
                    ModalityCounts = new Dictionary<string, int>(image.ModalityCounts ?? new Dictionary<string, int>()),
                },
                Links = new SeriesLinks
                {
                    Rtstructs = image.LinkedRtStructs.Select(rs => new LinkedStructureSet
                    {
                        SeriesUid = ids.Series(rs.SeriesInstanceUID),
                        SeriesDescription = rs.SeriesDescription,
                        MatchRule = rs.LinkMatchRule.ToString(),
                        RoiCount = rs.RoiNames?.Count ?? 0,
                        FrameOfReferenceMatch = FrameMatch(image, rs),
                    }).ToList(),
                    DefaultRtstruct = ids.Series(image.LinkedRtStruct?.SeriesInstanceUID),
                },
                Dose = image.LinkedRtDoses.Select(rd => new LinkedDose
                {
                    SeriesUid = ids.Series(rd.SeriesInstanceUID),
                    SeriesDescription = rd.SeriesDescription,
                    MatchRule = rd.LinkMatchRule.ToString(),
                    FileCount = rd.FilePaths?.Count ?? 0,
                    LinkedSeries = SeriesFlags.LinkedImageSeries(study, rd).Count(),
                    FrameOfReferenceMatch = FrameMatch(image, rd),
                }).ToList(),
            };
            foreach (var rs in image.LinkedRtStructs)
                series.Rois.AddRange(DescribeRois(rs, ids.Series(rs.SeriesInstanceUID), associations));

            // Flags, in FlagCodes.All order. The ones that mirror a badge test the scanned series
            // itself; their wording is built from a copy that, under anonymization, carries the
            // series hash and no file name (the same copy the plan document's warnings use).
            var text = CohortDocuments.ForAnonymizedText(image, seriesUid, ids.Anonymized);
            if (SeriesFlags.IsNonUniformZ(image, out _))
            {
                SeriesFlags.IsNonUniformZ(text, out string warning);
                context.Raise(series, FlagCodes.GeomNonUniformZ, seriesUid, null, null, warning);
            }
            if (SeriesFlags.HasMissingPositions(image, out _))
            {
                SeriesFlags.HasMissingPositions(text, out string warning);
                context.Raise(series, FlagCodes.GeomUnpositioned, seriesUid, null, null, warning);
            }
            if (orientation != null && orientation.IsTilted)
            {
                context.Raise(series, FlagCodes.GeomOblique, seriesUid, null, null, string.Format(CultureInfo.InvariantCulture,
                    "series {0}: the slice normal is {1:0.##} degrees off the patient z axis ({2}). Its slice spacing is " +
                    "measured along z and does not describe it, and contours are matched to its slices by z (toolkit issue 44).",
                    seriesUid, orientation.NormalTiltDegrees, orientation.Plane));
            }
            if (SeriesFlags.HasMixedOrientation(image))
            {
                context.Raise(series, FlagCodes.GeomMixedOrientation, seriesUid, null, null, string.Format(CultureInfo.InvariantCulture,
                    "series {0}: its files carry more than one ImageOrientationPatient, so they are not one stack of parallel " +
                    "slices; no orientation is reported for it.", seriesUid));
            }
            if (SeriesFlags.HasModalityMinority(image))
            {
                int total = image.ModalityCounts.Values.Sum();
                var others = image.ModalityCounts.Where(c => c.Key != image.Modality).ToList();
                context.Raise(series, FlagCodes.GeomModalityMinority, seriesUid, null, null, string.Format(CultureInfo.InvariantCulture,
                    "series {0}: {1} of {2} instance(s) carry another Modality ({3}); the series is {4} by majority.",
                    seriesUid, others.Sum(c => c.Value), total,
                    string.Join(", ", others.Select(c => c.Key + " " + c.Value.ToString(CultureInfo.InvariantCulture))),
                    image.Modality));
            }
            foreach (var rt in image.LinkedRtStructs.Concat(image.LinkedRtDoses).Where(SeriesFlags.IsLinkGuess))
            {
                string rtUid = ids.Series(rt.SeriesInstanceUID);
                context.Raise(series, FlagCodes.LinkFallback, seriesUid, rtUid, null, string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} ('{2}') was linked to series {3} by LargestSeriesFallback: neither its referenced series nor its " +
                    "frame of reference matched an image series of the study, so the study's largest series was assumed.",
                    rt.Modality, rtUid, rt.SeriesDescription, seriesUid));
            }
            foreach (var rt in image.LinkedRtStructs.Concat(image.LinkedRtDoses)
                         .Where(rt => SeriesFlags.HasFrameOfReferenceMismatch(image, rt)))
            {
                string rtUid = ids.Series(rt.SeriesInstanceUID);
                string frames = ids.Anonymized
                    ? "none of the frames of reference it names"
                    : string.Format(CultureInfo.InvariantCulture, "{0}, none of the frames of reference it names ({1})",
                        image.FrameOfReferenceUID, string.Join(", ", SeriesFlags.FramesOf(rt)));
                context.Raise(series, FlagCodes.LinkForMismatch, seriesUid, rtUid, null, string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} references series {2} by UID, but the series' frame of reference is {3}: its coordinates may " +
                    "not be the series'.", rt.Modality, rtUid, seriesUid, frames));
            }
            foreach (var rs in image.LinkedRtStructs.Where(rs => SeriesFlags.ReferencesAbsentSeries(study, rs)))
                RaiseReferencedSeriesAbsent(context, series, seriesUid, rs, ids.Series(rs.SeriesInstanceUID), ids);
            if (SeriesFlags.HasMultipleStructureSets(image))
            {
                context.Raise(series, FlagCodes.RtMultipleStructs, seriesUid, null, null, string.Format(CultureInfo.InvariantCulture,
                    "series {0}: {1} structure sets are linked to it. An export takes one: a --struct-series choice, then " +
                    "--struct-description's match, then the first linked ({2}), which the desktop app's Convert Selected always takes.",
                    seriesUid, image.LinkedRtStructs.Count, series.Links.DefaultRtstruct));
            }
            foreach (var rs in image.LinkedRtStructs)
                RaiseDuplicateNames(context, series, seriesUid, rs, ids.Series(rs.SeriesInstanceUID));

            return series;
        }

        /// <summary>
        /// The contours tier for one image series: every linked structure set read in full and its
        /// ROIs measured against the series' grid, and every linked dose audited against the
        /// series and the ROIs of its default structure set. Adds to <paramref name="series"/>.
        /// </summary>
        private static void DescribeContours(
            SeriesAnalysis series, DicomSeriesGroup image, AnalysisIdentifiers ids, FlagContext context,
            Dictionary<string, StructureSetContours> reads)
        {
            string seriesUid = series.SeriesUid;
            var grid = ImageGrid.FromSeries(image);
            RoiContours[] defaultRois = null;

            for (int s = 0; s < image.LinkedRtStructs.Count; s++)
            {
                var rs = image.LinkedRtStructs[s];
                string rtUid = ids.Series(rs.SeriesInstanceUID);
                var read = ReadOnce(rs, reads);
                var link = series.Links.Rtstructs[s];
                link.Contours = new StructureSetContourFacts
                {
                    Readable = read.Error == null,
                    Error = ids.Anonymized ? null : read.Error,
                    Rois = read.Rois.Count,
                    RoisWithContours = read.Rois.Count(r => r.HasContourSequence),
                    DuplicateContourItems = read.DuplicateContourItems,
                    OrphanContourItems = read.OrphanContourItems,
                };
                if (read.Error != null)
                {
                    context.Raise(series, FlagCodes.RtUnreadable, seriesUid, rtUid, null, string.Format(CultureInfo.InvariantCulture,
                        "structure set {0}: its contour data could not be read in full ({1}); a conversion of it fails.",
                        rtUid, ids.Anonymized ? "the reader's message is withheld under anonymization" : read.Error));
                    continue;
                }

                // The scan's ROI names are the StructureSetROISequence's, in file order; pair each
                // with the reader's ROI of the same name and occurrence.
                var entries = series.Rois.Where(r => r.StructureSetUid == rtUid).ToList();
                var matched = MatchRois(entries.Select(e => e.Name).ToList(), read);
                if (rs == image.LinkedRtStruct)
                    defaultRois = matched;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (matched[i] == null) continue;
                    var facts = RoiContourGeometry.Measure(matched[i], grid);
                    entries[i].Contours = facts;
                    RaiseRoiFlags(context, series, seriesUid, rtUid, entries[i].Name, facts, grid);
                }
            }

            var spacing = series.Geometry?.Spacing;
            for (int d = 0; d < image.LinkedRtDoses.Count; d++)
            {
                var rd = image.LinkedRtDoses[d];
                string rdUid = ids.Series(rd.SeriesInstanceUID);
                var rois = (defaultRois ?? new RoiContours[0]).Where(r => r != null)
                    .Select(r => new KeyValuePair<string, RoiContours>(r.Name, r));
                DoseFacts audit = rd.FilePaths == null || rd.FilePaths.Count == 0
                    ? new DoseFacts { Error = "the dose series has no file", RoiCoverage = null }
                    : DoseGridAudit.Audit(rd.FilePaths[0], grid, spacing, rois);
                if (audit.Error != null && ids.Anonymized)
                {
                    context.Raise(series, FlagCodes.RtUnreadable, seriesUid, rdUid, null, string.Format(CultureInfo.InvariantCulture,
                        "RTDOSE {0}: it could not be read in full (the reader's message is withheld under anonymization); a conversion of it fails.", rdUid));
                    audit.Error = "withheld under anonymization";
                }
                else if (audit.Error != null)
                {
                    context.Raise(series, FlagCodes.RtUnreadable, seriesUid, rdUid, null, string.Format(CultureInfo.InvariantCulture,
                        "RTDOSE {0}: it could not be read in full ({1}); a conversion of it fails.", rdUid, audit.Error));
                }
                series.Dose[d].Audit = audit;

                if (audit.OrientationMatchesImage == false)
                {
                    context.Raise(series, FlagCodes.DoseNotAligned, seriesUid, rdUid, null, string.Format(CultureInfo.InvariantCulture,
                        "RTDOSE {0}: its rows and columns do not run along series {1}'s; the dose grid is rotated against the image.",
                        rdUid, seriesUid));
                }
                foreach (var coverage in (audit.RoiCoverage ?? new List<RoiDoseCoverage>()).Where(c => !c.Covered))
                {
                    context.Raise(series, FlagCodes.DoseNotCoveringRoi, seriesUid, rdUid, coverage.Roi, string.Format(CultureInfo.InvariantCulture,
                        "RTDOSE {0}: ROI '{1}' extends outside the dose grid ({2:0.##}% of its contour points inside); a DVH over it misses dose.",
                        rdUid, coverage.Roi, coverage.PointsInside * 100));
                }
            }

            // Flags are raised structure set by structure set, then dose by dose; keep the series'
            // list in FlagCodes.All order as the header tier's is.
            var ordered = series.Flags.OrderBy(FlagCodes.OrderOf).ToList();
            series.Flags.Clear();
            series.Flags.AddRange(ordered);
        }

        private static StructureSetContours ReadOnce(DicomSeriesGroup rtStruct, Dictionary<string, StructureSetContours> reads)
        {
            string path = rtStruct.FilePaths?.FirstOrDefault();
            if (path == null)
                return new StructureSetContours { Error = "the structure set series has no file" };
            if (!reads.TryGetValue(path, out var read))
            {
                read = RtStructContourReader.Read(path);
                reads[path] = read;
            }
            return read;
        }

        /// <summary>For each name, in order, the reader's ROI of that name and occurrence; null where it has none.</summary>
        internal static RoiContours[] MatchRois(List<string> names, StructureSetContours read)
        {
            var byName = read.Rois.GroupBy(r => r.Name ?? "", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<RoiContours>(g), StringComparer.Ordinal);
            return names.Select(n => byName.TryGetValue(n ?? "", out var queue) && queue.Count > 0 ? queue.Dequeue() : null).ToArray();
        }

        private static void RaiseRoiFlags(
            FlagContext context, SeriesAnalysis series, string seriesUid, string rtUid, string roi, RoiContourFacts facts, ImageGrid grid)
        {
            if (facts.Empty)
            {
                string why = facts.Contours == 0
                    ? "no contour references it"
                    : string.Format(CultureInfo.InvariantCulture, "{0} contour(s): parse-failed={1}, too-few-points={2}",
                        facts.Contours, facts.ParseFailed, facts.TooFewPoints);
                context.Raise(series, FlagCodes.RoiEmpty, seriesUid, rtUid, roi, string.Format(CultureInfo.InvariantCulture,
                    "structure set {0}, ROI '{1}': no contour has a point ({2}), so an export writes no mask for it.", rtUid, roi, why));
                return;
            }
            if (facts.Degenerate)
            {
                context.Raise(series, FlagCodes.RoiDegenerate, seriesUid, rtUid, roi, string.Format(CultureInfo.InvariantCulture,
                    "structure set {0}, ROI '{1}': all {2} usable contour(s) are degenerate (a closed contour of fewer than three " +
                    "points or no area, or an open contour of one point) [types: {3}].",
                    rtUid, roi, facts.UsableContours, string.Join(", ", facts.GeometricTypes.Keys)));
            }
            if (facts.OffPlaneContours > 0)
            {
                context.Raise(series, FlagCodes.RoiOffPlane, seriesUid, rtUid, roi, string.Format(CultureInfo.InvariantCulture,
                    "structure set {0}, ROI '{1}': {2} planar contour(s) are not on one z plane within {3} mm of an image slice of " +
                    "series {4}; the rasterizer fills a planar contour on its nearest slice.",
                    rtUid, roi, facts.OffPlaneContours, RoiContourGeometry.OffPlaneToleranceMm, seriesUid));
            }
            if (facts.OutsideFovContours > 0)
            {
                context.Raise(series, FlagCodes.RoiOutsideFov, seriesUid, rtUid, roi, string.Format(CultureInfo.InvariantCulture,
                    "structure set {0}, ROI '{1}': {2} of {3} usable contour(s) lie wholly outside the voxels of series {4} " +
                    "({5} x {6}, z {7:0.##} to {8:0.##} mm): the structure set may be matched to the wrong image series, or its " +
                    "contours lie outside the image extent.",
                    rtUid, roi, facts.OutsideFovContours, facts.UsableContours, seriesUid, grid.Columns, grid.Rows, grid.ZLow, grid.ZHigh));
            }
        }

        private static void RaiseReferencedSeriesAbsent(
            FlagContext context, SeriesAnalysis series, string seriesUid, DicomSeriesGroup rtStruct, string rtUid,
            AnalysisIdentifiers ids)
        {
            string outcome = seriesUid == null
                ? "it is linked to nothing"
                : string.Format(CultureInfo.InvariantCulture, "it was linked to series {0} by {1} instead", seriesUid, rtStruct.LinkMatchRule);
            context.Raise(series, FlagCodes.LinkReferencedSeriesAbsent, seriesUid, rtUid, null, string.Format(CultureInfo.InvariantCulture,
                "{0} {1} ('{2}') references image series {3}, which its study does not hold (not in the input, or in " +
                "another study); {4}.", rtStruct.Modality, rtUid, rtStruct.SeriesDescription,
                ids.Series(rtStruct.ReferencedSeriesUID), outcome));
        }

        private static void RaiseDuplicateNames(
            FlagContext context, SeriesAnalysis series, string seriesUid, DicomSeriesGroup rtStruct, string rtUid)
        {
            foreach (var name in rtStruct.DuplicateRoiNames ?? new List<string>())
            {
                context.Raise(series, FlagCodes.RoiDuplicateName, seriesUid, rtUid, name, string.Format(CultureInfo.InvariantCulture,
                    "structure set {0}: more than one ROI is named '{1}'. DICOM does not require ROI names to be unique; an " +
                    "export keeps '{1}' for the lowest ROINumber and suffixes the rest with their ROINumber.", rtUid, name));
            }
        }

        /// <summary>
        /// Every ROI of one structure set: its canonical name, whether an association covers it,
        /// and what the export does with it with and without --only-associated-rois. That is the
        /// export's resolver (<see cref="NiftiConversionService.ResolveRoiNames"/>) read back the
        /// way the rasterizer reads it: a DICOM name looked up ignoring case, the last mapping
        /// winning (RtStructMaskService's reverse lookup), so two names differing only in case
        /// both export, as they do.
        /// </summary>
        private static IEnumerable<RoiEntry> DescribeRois(DicomSeriesGroup rtStruct, string rtUid, List<RoiAssociation> associations)
        {
            var names = rtStruct.RoiNames ?? new List<string>();
            var outputByDicomName = OutputByDicomName(names, associations, exportUnmatched: true);
            var keptIfOnlyAssociated = OutputByDicomName(names, associations, exportUnmatched: false);

            return names.Select(name => new RoiEntry
            {
                StructureSetUid = rtUid,
                Name = name,
                CanonicalName = RoiNameMatcher.ResolveToCanonical(name, associations),
                Associated = associations.Count > 0 && !SeriesFlags.IsUnassociated(name, associations),
                OutputName = outputByDicomName.TryGetValue(name, out var output) ? output : null,
                KeptIfOnlyAssociated = keptIfOnlyAssociated.ContainsKey(name),
                Duplicate = rtStruct.DuplicateRoiNames?.Contains(name) ?? false,
            }).ToList();
        }

        /// <summary>The rasterizer's DICOM name → output name lookup for one structure set (RtStructMaskService.cs:85-89).</summary>
        internal static Dictionary<string, string> OutputByDicomName(
            List<string> dicomRoiNames, List<RoiAssociation> associations, bool exportUnmatched)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in NiftiConversionService.ResolveRoiNames(dicomRoiNames, associations, exportUnmatched))
                lookup[mapping.Value] = mapping.Key;
            return lookup;
        }

        /// <summary>Whether the image's frame is one the RT object names; null when either names none.</summary>
        private static bool? FrameMatch(DicomSeriesGroup image, DicomSeriesGroup rtObject)
        {
            var frames = SeriesFlags.FramesOf(rtObject);
            if (string.IsNullOrEmpty(image.FrameOfReferenceUID) || frames.Count == 0)
                return null;
            return frames.Contains(image.FrameOfReferenceUID, StringComparer.Ordinal);
        }

        private static CohortAnalysisSummary Summarize(DicomScanResult scan, List<AnalysisFlag> flags, AnalysisTier tier)
        {
            var all = scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series).ToList();
            var summary = new CohortAnalysisSummary
            {
                Patients = scan.Patients.Count,
                Studies = scan.Patients.Sum(p => p.Studies.Count),
                ImageSeries = all.Count(SeriesFlags.IsImageSeries),
                Rtstructs = all.Count(s => s.Modality == "RTSTRUCT"),
                Rtdoses = all.Count(s => s.Modality == "RTDOSE"),
            };
            summary.OtherObjects = all.Count - summary.ImageSeries - summary.Rtstructs - summary.Rtdoses;

            foreach (var severity in new[] { FlagSeverity.Serious, FlagSeverity.Warning, FlagSeverity.Info })
                summary.FlagsBySeverity[severity] = flags.Count(f => f.Severity == severity);
            foreach (var definition in FlagCodes.All.Where(d => FlagCodes.RaisedAt(d, tier)))
                summary.FlagsByCode[definition.Code] = flags.Count(f => f.Code == definition.Code);
            return summary;
        }

        /// <summary>Raises flags for one study, into the cohort's list and the series' own.</summary>
        private sealed class FlagContext
        {
            private readonly List<AnalysisFlag> _flags;

            public FlagContext(List<AnalysisFlag> flags, string patientId, string studyUid)
            {
                _flags = flags;
                PatientId = patientId;
                StudyUid = studyUid;
            }

            public string PatientId { get; }
            public string StudyUid { get; }

            public void Raise(SeriesAnalysis series, string code, string seriesUid, string objectUid, string roi, string message)
            {
                _flags.Add(new AnalysisFlag
                {
                    Code = code,
                    Severity = FlagCodes.Get(code).Severity,
                    PatientId = PatientId,
                    StudyUid = StudyUid,
                    SeriesUid = seriesUid,
                    ObjectUid = objectUid,
                    Roi = roi,
                    Message = message,
                });
                if (series != null && !series.Flags.Contains(code))
                    series.Flags.Add(code);
            }
        }
    }
}
