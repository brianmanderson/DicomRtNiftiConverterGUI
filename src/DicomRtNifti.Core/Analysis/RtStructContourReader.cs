using System;
using System.Collections.Generic;
using FellowOakDicom;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>One structure set's contours as the contours tier reads them.</summary>
    public sealed class StructureSetContours
    {
        /// <summary>Every ROI of the StructureSetROISequence, in file order.</summary>
        public List<RoiContours> Rois { get; } = new List<RoiContours>();

        /// <summary>
        /// Why the file could not be read in full (fo-dicom's message), or null. When set,
        /// <see cref="Rois"/> is empty: a conversion of this structure set fails the same way.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// ROIContourSequence items that reference a ROINumber an earlier item already did. The
        /// sequence carries one item per ROI; the first is read and the rest ignored, as the
        /// rasterizer does.
        /// </summary>
        public int DuplicateContourItems { get; set; }

        /// <summary>ROIContourSequence items whose ReferencedROINumber is missing or unparseable, or names no ROI.</summary>
        public int OrphanContourItems { get; set; }
    }

    /// <summary>One ROI and the contours of its ROIContourSequence item.</summary>
    public sealed class RoiContours
    {
        public int RoiNumber { get; set; }
        public string Name { get; set; }

        /// <summary>Whether a ROIContourSequence item references the ROI and carries a readable ContourSequence.</summary>
        public bool HasContourSequence { get; set; }

        /// <summary>Every item of the ContourSequence, in file order, the unreadable ones included.</summary>
        public List<ContourRecord> Contours { get; } = new List<ContourRecord>();
    }

    /// <summary>One item of a ContourSequence.</summary>
    public sealed class ContourRecord
    {
        /// <summary>ContourGeometricType, trimmed and upper-cased; CLOSED_PLANAR when absent or unreadable.</summary>
        public string GeometricType { get; set; }

        /// <summary>ContourData, [x0, y0, z0, x1, ...] in patient mm; null when <see cref="ParseFailed"/>.</summary>
        public double[] Points { get; set; }

        /// <summary>ContourData is absent or does not parse as numbers.</summary>
        public bool ParseFailed { get; set; }

        /// <summary>ContourData has fewer than three numbers: not one point.</summary>
        public bool TooFewPoints => !ParseFailed && (Points == null || Points.Length < 3);

        /// <summary>Whole points in <see cref="Points"/> (a trailing partial triple is ignored, as the rasterizer ignores it).</summary>
        public int PointCount => Points == null ? 0 : Points.Length / 3;
    }

    /// <summary>
    /// A full fo-dicom read of one RTSTRUCT's contour data, without rasterizing: ROI number, name,
    /// and each contour's geometric type and points. The scan stops before ROIContourSequence
    /// (contour points were half its parse time), so this is the contours tier's added cost.
    ///
    /// It walks the sequences the way <c>RtStructMaskService.RasterizeRois</c> walks them, so its
    /// per-ROI buckets — contours, parse failures, too-few-point contours, geometric types — are
    /// the inputs of the rasterizer's "Skipping ROI" diagnostic, and the agreement tests hold them
    /// equal. It is a second parser (web plan risk R-3): it reports what the file holds, and does
    /// not decide whether the rasterizer will accept a contour.
    /// </summary>
    public static class RtStructContourReader
    {
        public const string DefaultGeometricType = "CLOSED_PLANAR";

        public static StructureSetContours Read(string rtStructPath)
        {
            if (rtStructPath == null) throw new ArgumentNullException(nameof(rtStructPath));

            DicomDataset ds;
            try
            {
                ds = DicomFile.Open(rtStructPath).Dataset;
            }
            catch (Exception ex)
            {
                return new StructureSetContours { Error = ex.Message };
            }

            try
            {
                return Read(ds);
            }
            catch (Exception ex)
            {
                // fo-dicom parses sequences lazily in places; a damaged one surfaces here.
                return new StructureSetContours { Error = ex.Message };
            }
        }

        public static StructureSetContours Read(DicomDataset ds)
        {
            if (ds == null) throw new ArgumentNullException(nameof(ds));
            var result = new StructureSetContours();

            var byNumber = new Dictionary<int, RoiContours>();
            if (ds.Contains(DicomTag.StructureSetROISequence))
            {
                foreach (var item in ds.GetSequence(DicomTag.StructureSetROISequence))
                {
                    int number;
                    try { number = item.GetSingleValue<int>(DicomTag.ROINumber); }
                    catch (Exception) { continue; }

                    var roi = new RoiContours { RoiNumber = number, Name = item.GetSingleValueOrDefault(DicomTag.ROIName, "") };
                    result.Rois.Add(roi);
                    // A repeated ROINumber: the rasterizer's map keeps the last name; the contours follow it.
                    byNumber[number] = roi;
                }
            }

            if (!ds.Contains(DicomTag.ROIContourSequence))
                return result;

            var seen = new HashSet<int>();
            foreach (var item in ds.GetSequence(DicomTag.ROIContourSequence))
            {
                int number;
                try { number = item.GetSingleValue<int>(DicomTag.ReferencedROINumber); }
                catch (Exception) { result.OrphanContourItems++; continue; }

                if (!byNumber.TryGetValue(number, out var roi)) { result.OrphanContourItems++; continue; }
                if (!seen.Add(number)) { result.DuplicateContourItems++; continue; }

                if (!item.Contains(DicomTag.ContourSequence)) continue;
                DicomSequence contours;
                try { contours = item.GetSequence(DicomTag.ContourSequence); }
                catch (Exception) { continue; }

                roi.HasContourSequence = true;
                foreach (var contour in contours)
                    roi.Contours.Add(ReadContour(contour));
            }
            return result;
        }

        private static ContourRecord ReadContour(DicomDataset contour)
        {
            string type = DefaultGeometricType;
            try { type = contour.GetSingleValueOrDefault(DicomTag.ContourGeometricType, DefaultGeometricType); }
            catch (Exception) { /* the rasterizer's default */ }

            var record = new ContourRecord { GeometricType = (type ?? DefaultGeometricType).Trim().ToUpperInvariant() };
            try
            {
                record.Points = contour.GetValues<double>(DicomTag.ContourData);
            }
            catch (Exception)
            {
                record.ParseFailed = true;
            }
            return record;
        }
    }
}
