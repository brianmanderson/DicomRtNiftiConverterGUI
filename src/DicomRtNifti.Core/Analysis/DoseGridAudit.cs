using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>One RTDOSE's grid, read from its header and checked against its image series (catalogue item 7).</summary>
    public sealed class DoseFacts
    {
        /// <summary>Why the file could not be read (fo-dicom's message), or null. When set, every other field is empty.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }

        /// <summary>DoseUnits: GY or RELATIVE.</summary>
        public string DoseUnits { get; set; }

        /// <summary>DoseType: PHYSICAL, EFFECTIVE or ERROR.</summary>
        public string DoseType { get; set; }

        /// <summary>DoseSummationType: PLAN, BEAM, FRACTION, ...</summary>
        public string DoseSummationType { get; set; }

        public int? Rows { get; set; }
        public int? Columns { get; set; }
        public int? Frames { get; set; }

        /// <summary>[x, y, z] mm (<see cref="MetadataComputedValues.DoseVoxelSize"/>).</summary>
        public double[] VoxelSizeMm { get; set; }

        /// <summary>The image series' [x, y, z] spacing, for comparison.</summary>
        public double[] ImageVoxelSizeMm { get; set; }

        /// <summary>Stored maximum × DoseGridScaling (<see cref="MetadataComputedValues.MaxDose"/>); null for compressed or absent pixel data.</summary>
        public double? MaxDose { get; set; }

        /// <summary>[x, y, z] of the box the dose voxels cover, mm; null without a usable grid.</summary>
        public double[] ExtentMinMm { get; set; }

        public double[] ExtentMaxMm { get; set; }

        /// <summary>
        /// The dose rows and columns run along the image's (null when either orientation is
        /// unknown). False raises DOSE_NOT_ALIGNED: the grid is rotated against the image.
        /// </summary>
        public bool? OrientationMatchesImage { get; set; }

        /// <summary>
        /// The dose voxels are the image's: same orientation, spacing and voxel centres. False is
        /// usual — a dose grid is coarser than the CT and resampled onto it — and raises nothing.
        /// </summary>
        public bool? SameGridAsImage { get; set; }

        /// <summary>Each ROI of the image's default structure set, against the dose extent; empty when either is unknown.</summary>
        public List<RoiDoseCoverage> RoiCoverage { get; set; } = new List<RoiDoseCoverage>();
    }

    public sealed class RoiDoseCoverage
    {
        public string Roi { get; set; }

        /// <summary>Contour points inside the dose extent / contour points, to 4 decimals.</summary>
        public double PointsInside { get; set; }

        /// <summary>Every contour point lies inside the dose extent; false raises DOSE_NOT_COVERING_ROI.</summary>
        public bool Covered { get; set; }
    }

    /// <summary>
    /// The dose audit: an RTDOSE's units, type, voxel size and maximum, and whether its grid is
    /// aligned with the image series and covers the ROIs a DVH would be computed over. Reads the
    /// dose file in full (the maximum needs its pixels); no SimpleITK.
    /// </summary>
    public static class DoseGridAudit
    {
        /// <summary>How far two direction cosines may differ and still be one orientation.</summary>
        public const double OrientationTolerance = 1e-4;

        /// <summary>How far, in mm, a dose voxel centre may sit from the image's and still be the same grid.</summary>
        public const double GridToleranceMm = 0.01;

        public static DoseFacts Audit(string rtDosePath, ImageGrid image, double[] imageSpacing,
            IEnumerable<KeyValuePair<string, RoiContours>> rois)
        {
            DicomDataset ds;
            try
            {
                ds = DicomFile.Open(rtDosePath).Dataset;
            }
            catch (Exception ex)
            {
                return new DoseFacts { Error = ex.Message, RoiCoverage = null };
            }
            return Audit(ds, image, imageSpacing, rois);
        }

        public static DoseFacts Audit(DicomDataset ds, ImageGrid image, double[] imageSpacing,
            IEnumerable<KeyValuePair<string, RoiContours>> rois)
        {
            if (ds == null) throw new ArgumentNullException(nameof(ds));
            var facts = new DoseFacts
            {
                DoseUnits = Text(ds, DicomTag.DoseUnits),
                DoseType = Text(ds, DicomTag.DoseType),
                DoseSummationType = Text(ds, DicomTag.DoseSummationType),
                Rows = Int(ds, DicomTag.Rows),
                Columns = Int(ds, DicomTag.Columns),
                Frames = Int(ds, DicomTag.NumberOfFrames),
                VoxelSizeMm = ImageGeometry.Round(MetadataComputedValues.DoseVoxelSize(ds) as double[]),
                ImageVoxelSizeMm = imageSpacing,
                MaxDose = ImageGeometry.Round(MetadataComputedValues.MaxDose(ds) as double?),
            };

            var grid = DoseGrid.From(ds);
            if (grid == null)
                return facts;

            facts.ExtentMinMm = ImageGeometry.Round(grid.ExtentMin);
            facts.ExtentMaxMm = ImageGeometry.Round(grid.ExtentMax);

            if (image != null)
            {
                bool oriented = Close(grid.Row, image.RowDirection, OrientationTolerance)
                    && Close(grid.Column, image.ColumnDirection, OrientationTolerance);
                facts.OrientationMatchesImage = oriented;
                facts.SameGridAsImage = oriented && SameVoxels(grid, image);
            }

            foreach (var roi in rois ?? Enumerable.Empty<KeyValuePair<string, RoiContours>>())
            {
                int total = 0, inside = 0;
                foreach (var contour in roi.Value.Contours.Where(c => !c.ParseFailed && !c.TooFewPoints))
                {
                    for (int i = 0; i < contour.PointCount; i++)
                    {
                        total++;
                        if (grid.Contains(contour.Points[i * 3], contour.Points[i * 3 + 1], contour.Points[i * 3 + 2]))
                            inside++;
                    }
                }
                if (total == 0) continue;
                facts.RoiCoverage.Add(new RoiDoseCoverage
                {
                    Roi = roi.Key,
                    PointsInside = Math.Round((double)inside / total, 4),
                    Covered = inside == total,
                });
            }
            return facts;
        }

        /// <summary>Same spacing, and the image's first voxel centre on a dose voxel centre in every axis.</summary>
        private static bool SameVoxels(DoseGrid dose, ImageGrid image)
        {
            if (Math.Abs(dose.ColumnSpacing - image.ColumnSpacing) > GridToleranceMm
                || Math.Abs(dose.RowSpacing - image.RowSpacing) > GridToleranceMm)
                return false;

            var zs = image.SliceZs;
            var frames = dose.FrameOffsets.Select(o => dose.Position[2] + o).ToArray();
            if (zs.Length != frames.Length) return false;
            for (int i = 0; i < zs.Length; i++)
                if (Math.Abs(zs[i] - frames[i]) > GridToleranceMm) return false;

            double c = image.ColumnIndex(dose.Position[0], dose.Position[1], dose.Position[2]);
            double r = image.RowIndex(dose.Position[0], dose.Position[1], dose.Position[2]);
            return Math.Abs(c * image.ColumnSpacing) <= GridToleranceMm && Math.Abs(r * image.RowSpacing) <= GridToleranceMm
                && dose.Columns == image.Columns && dose.Rows == image.Rows;
        }

        private static bool Close(double[] a, double[] b, double tolerance)
        {
            for (int i = 0; i < 3; i++)
                if (Math.Abs(a[i] - b[i]) > tolerance) return false;
            return true;
        }

        private static string Text(DicomDataset ds, DicomTag tag)
        {
            try { return ds.GetSingleValueOrDefault<string>(tag, null)?.Trim(); }
            catch (Exception) { return null; }
        }

        private static int? Int(DicomDataset ds, DicomTag tag)
        {
            try { return ds.Contains(tag) ? ds.GetSingleValue<int>(tag) : (int?)null; }
            catch (Exception) { return null; }
        }

        /// <summary>A dose grid from its header: position, orientation, spacing, size and frame offsets.</summary>
        private sealed class DoseGrid
        {
            public double[] Position, Row, Column, Normal;
            public double RowSpacing, ColumnSpacing;
            public int Rows, Columns;

            /// <summary>Offsets of each frame along the slice normal from <see cref="Position"/>, mm.</summary>
            public double[] FrameOffsets;

            public double[] ExtentMin, ExtentMax;

            public static DoseGrid From(DicomDataset ds)
            {
                try
                {
                    if (!ds.Contains(DicomTag.ImagePositionPatient) || !ds.Contains(DicomTag.ImageOrientationPatient)
                        || !ds.Contains(DicomTag.PixelSpacing))
                        return null;
                    var position = ds.GetValues<double>(DicomTag.ImagePositionPatient);
                    var iop = ds.GetValues<double>(DicomTag.ImageOrientationPatient);
                    var spacing = ds.GetValues<double>(DicomTag.PixelSpacing);
                    int rows = ds.GetSingleValue<int>(DicomTag.Rows), columns = ds.GetSingleValue<int>(DicomTag.Columns);
                    if (position.Length < 3 || iop.Length < 6 || spacing.Length < 2 || rows < 1 || columns < 1)
                        return null;

                    var grid = new DoseGrid
                    {
                        Position = position.Take(3).ToArray(),
                        Row = iop.Take(3).ToArray(),
                        Column = iop.Skip(3).Take(3).ToArray(),
                        RowSpacing = spacing[0],
                        ColumnSpacing = spacing[1],
                        Rows = rows,
                        Columns = columns,
                    };
                    grid.Normal = Cross(grid.Row, grid.Column);

                    // GridFrameOffsetVector: relative to the first frame when its first value is 0
                    // (the usual form), else the frames' own normal coordinates (PS3.3 C.8.8.3.2).
                    double[] offsets = ds.Contains(DicomTag.GridFrameOffsetVector)
                        ? ds.GetValues<double>(DicomTag.GridFrameOffsetVector)
                        : new[] { 0.0 };
                    if (offsets.Length == 0) offsets = new[] { 0.0 };
                    if (offsets[0] != 0)
                    {
                        double origin = Dot(grid.Position, grid.Normal);
                        offsets = offsets.Select(o => o - origin).ToArray();
                    }
                    grid.FrameOffsets = offsets;
                    grid.Bounds();
                    return grid;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>The dose voxel containing a patient point, half a voxel past the edge centres included.</summary>
            public bool Contains(double x, double y, double z)
            {
                var d = new[] { x - Position[0], y - Position[1], z - Position[2] };
                double c = Dot(d, Row) / ColumnSpacing, r = Dot(d, Column) / RowSpacing, n = Dot(d, Normal);
                if (c < -0.5 || c > Columns - 0.5 || r < -0.5 || r > Rows - 0.5) return false;
                FrameRange(out double lo, out double hi);
                return n >= lo && n <= hi;
            }

            private void FrameRange(out double lo, out double hi)
            {
                double min = FrameOffsets.Min(), max = FrameOffsets.Max();
                double half = FrameOffsets.Length < 2 ? 0 : Math.Abs(FrameOffsets[1] - FrameOffsets[0]) / 2;
                lo = min - half;
                hi = max + half;
            }

            private void Bounds()
            {
                FrameRange(out double lo, out double hi);
                ExtentMin = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                ExtentMax = new[] { double.MinValue, double.MinValue, double.MinValue };
                foreach (double c in new[] { -0.5, Columns - 0.5 })
                foreach (double r in new[] { -0.5, Rows - 0.5 })
                foreach (double n in new[] { lo, hi })
                {
                    for (int a = 0; a < 3; a++)
                    {
                        double v = Position[a] + c * ColumnSpacing * Row[a] + r * RowSpacing * Column[a] + n * Normal[a];
                        ExtentMin[a] = Math.Min(ExtentMin[a], v);
                        ExtentMax[a] = Math.Max(ExtentMax[a], v);
                    }
                }
            }

            private static double[] Cross(double[] a, double[] b) => new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0],
            };

            private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }
    }
}
