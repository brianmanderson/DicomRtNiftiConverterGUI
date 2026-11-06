using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using FellowOakDicom;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// An image series' voxel grid as headers give it: the first positioned slice's
    /// ImagePositionPatient and the series' orientation, pixel spacing, rows, columns and slice
    /// positions. The contours tier checks contours against it without decoding a pixel.
    /// Only for a series whose slice normal is the patient z axis: slice positions are z values.
    /// </summary>
    public sealed class ImageGrid
    {
        public ImageGrid(double[] position, double[] imageOrientationPatient, double[] pixelSpacing,
            int rows, int columns, IEnumerable<double> sliceZs)
        {
            if (position == null || position.Length < 3) throw new ArgumentException("Three coordinates expected.", nameof(position));
            if (imageOrientationPatient == null || imageOrientationPatient.Length < 6) throw new ArgumentException("Six direction cosines expected.", nameof(imageOrientationPatient));
            if (pixelSpacing == null || pixelSpacing.Length < 2) throw new ArgumentException("Row and column spacing expected.", nameof(pixelSpacing));

            Position = position.Take(3).ToArray();
            RowDirection = imageOrientationPatient.Take(3).ToArray();
            ColumnDirection = imageOrientationPatient.Skip(3).Take(3).ToArray();
            RowSpacing = pixelSpacing[0];
            ColumnSpacing = pixelSpacing[1];
            Rows = rows;
            Columns = columns;
            SliceZs = (sliceZs ?? Enumerable.Empty<double>()).OrderBy(z => z).ToArray();
        }

        public double[] Position { get; }

        /// <summary>Direction of increasing column index (IOP's first triple).</summary>
        public double[] RowDirection { get; }

        /// <summary>Direction of increasing row index (IOP's second triple).</summary>
        public double[] ColumnDirection { get; }

        /// <summary>Spacing between rows (PixelSpacing[0]), mm.</summary>
        public double RowSpacing { get; }

        /// <summary>Spacing between columns (PixelSpacing[1]), mm.</summary>
        public double ColumnSpacing { get; }

        public int Rows { get; }
        public int Columns { get; }

        /// <summary>Positioned slices' z, ascending.</summary>
        public double[] SliceZs { get; }

        /// <summary>Continuous column index of a patient point (0 at the first voxel's centre).</summary>
        public double ColumnIndex(double x, double y, double z) =>
            Dot(x - Position[0], y - Position[1], z - Position[2], RowDirection) / ColumnSpacing;

        /// <summary>Continuous row index of a patient point.</summary>
        public double RowIndex(double x, double y, double z) =>
            Dot(x - Position[0], y - Position[1], z - Position[2], ColumnDirection) / RowSpacing;

        /// <summary>
        /// The z range the slices' voxels cover: each end slice reaches half its gap past its
        /// centre. A single slice covers its own plane only.
        /// </summary>
        public double ZLow => SliceZs.Length < 2 ? SliceZs[0] : SliceZs[0] - (SliceZs[1] - SliceZs[0]) / 2;

        public double ZHigh
        {
            get
            {
                int n = SliceZs.Length;
                return n < 2 ? SliceZs[0] : SliceZs[n - 1] + (SliceZs[n - 1] - SliceZs[n - 2]) / 2;
            }
        }

        /// <summary>Distance from <paramref name="z"/> to the nearest slice, and that slice's index.</summary>
        public double NearestSlice(double z, out int index)
        {
            index = 0;
            double best = Math.Abs(SliceZs[0] - z);
            for (int i = 1; i < SliceZs.Length; i++)
            {
                double d = Math.Abs(SliceZs[i] - z);
                if (d < best) { best = d; index = i; }
            }
            return best;
        }

        /// <summary>
        /// The grid of an image series, from its scanned model and the header of one positioned
        /// slice (read without pixel data). Null when the series lacks what a grid needs, its files
        /// disagree on orientation, or its slice normal is not the z axis (GEOM_OBLIQUE,
        /// GEOM_MIXED_ORIENTATION): contours are then not checked against it.
        /// </summary>
        public static ImageGrid FromSeries(DicomSeriesGroup series)
        {
            if (series == null) return null;
            if (series.OrientationVaries || series.ImageOrientationPatient == null || series.PixelSpacing == null
                || !series.Rows.HasValue || !series.Columns.HasValue || series.SlicePositions == null
                || series.SlicePositions.Count == 0 || series.NumberOfFrames > 1)
                return null;
            var orientation = SeriesOrientation.FromImageOrientation(series.ImageOrientationPatient);
            if (orientation == null || orientation.IsTilted) return null;

            foreach (var path in series.FilePaths ?? new List<string>())
            {
                try
                {
                    var ds = DicomFile.Open(path, FileReadOption.SkipLargeTags).Dataset;
                    if (!ds.Contains(DicomTag.ImagePositionPatient)) continue;
                    var position = ds.GetValues<double>(DicomTag.ImagePositionPatient);
                    if (position == null || position.Length < 3) continue;
                    return new ImageGrid(position, series.ImageOrientationPatient, series.PixelSpacing,
                        series.Rows.Value, series.Columns.Value, series.SlicePositions);
                }
                catch (Exception)
                {
                    // An unreadable slice; try the next.
                }
            }
            return null;
        }

        private static double Dot(double x, double y, double z, double[] d) => x * d[0] + y * d[1] + z * d[2];
    }

    /// <summary>
    /// One ROI's contours, measured from the contour data alone (catalogue item 6). Counts come
    /// from <see cref="RtStructContourReader"/>; the checks against the image grid are geometry,
    /// not the rasterizer's verdict: a contour reported wholly outside the field of view is one
    /// whose bounding box misses the voxels, while the rasterizer rejects only contours more than
    /// a grid's width away and maps any planar contour to its nearest slice.
    /// </summary>
    public sealed class RoiContourFacts
    {
        /// <summary>Items of the ROI's ContourSequence; 0 when none references the ROI.</summary>
        public int Contours { get; set; }

        /// <summary>Contours with at least one point.</summary>
        public int UsableContours { get; set; }

        /// <summary>ContourData missing or not numbers (the rasterizer's parse-failed).</summary>
        public int ParseFailed { get; set; }

        /// <summary>ContourData with fewer than three numbers (the rasterizer's too-few-points).</summary>
        public int TooFewPoints { get; set; }

        /// <summary>Usable contours that enclose or trace nothing: a closed contour of fewer than three points or no area, an open one of one point.</summary>
        public int DegenerateContours { get; set; }

        public int Points { get; set; }

        /// <summary>Contours per ContourGeometricType, ascending by type.</summary>
        public SortedDictionary<string, int> GeometricTypes { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Distinct z planes of the planar contours (to 0.01 mm).</summary>
        public int Planes { get; set; }

        public double? ZMin { get; set; }
        public double? ZMax { get; set; }
        public double? ZExtentMm { get; set; }

        /// <summary>[x, y, z] of the usable points' bounding box, mm.</summary>
        public double[] BoundsMinMm { get; set; }

        public double[] BoundsMaxMm { get; set; }

        /// <summary>Whether the image grid was known, so the four counts below were computed; false for an oblique or incomplete series.</summary>
        public bool GridChecked { get; set; }

        /// <summary>Image slices a planar contour lies nearest to, within the field of view.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? SlicesWithContours { get; set; }

        /// <summary>
        /// Planar contours whose points are not one z plane, or whose plane is more than
        /// <see cref="RoiContourGeometry.OffPlaneToleranceMm"/> from every slice: the rasterizer
        /// fills them on the nearest slice.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? OffPlaneContours { get; set; }

        /// <summary>Usable contours whose bounding box misses the image's voxels entirely.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? OutsideFovContours { get; set; }

        /// <summary>Usable contours with some points outside the field of view and some inside it (a body clipped by the FOV).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? PartlyOutsideFovContours { get; set; }

        /// <summary>No usable contour: an export writes no mask for it (ROI_EMPTY).</summary>
        public bool Empty { get; set; }

        /// <summary>Usable contours, every one degenerate (ROI_DEGENERATE).</summary>
        public bool Degenerate { get; set; }
    }

    /// <summary>Measures one ROI's contours, optionally against an image grid.</summary>
    public static class RoiContourGeometry
    {
        /// <summary>
        /// How far a planar contour's points may sit from one z plane, and that plane from a slice,
        /// before it counts as off-plane. Planning systems write contours on slice planes; this
        /// only absorbs decimal-string rounding.
        /// </summary>
        public const double OffPlaneToleranceMm = 0.05;

        /// <summary>Below this, in mm², a closed planar contour encloses no area.</summary>
        public const double ZeroAreaMm2 = 1e-6;

        public static RoiContourFacts Measure(RoiContours roi, ImageGrid grid)
        {
            if (roi == null) throw new ArgumentNullException(nameof(roi));
            var facts = new RoiContourFacts { Contours = roi.Contours.Count, GridChecked = grid != null };

            double[] min = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] max = { double.MinValue, double.MinValue, double.MinValue };
            var planes = new HashSet<long>();
            var slices = new HashSet<int>();
            int offPlane = 0, outside = 0, partly = 0;

            foreach (var contour in roi.Contours)
            {
                facts.GeometricTypes.TryGetValue(contour.GeometricType, out int seen);
                facts.GeometricTypes[contour.GeometricType] = seen + 1;
                if (contour.ParseFailed) { facts.ParseFailed++; continue; }
                if (contour.TooFewPoints) { facts.TooFewPoints++; continue; }

                facts.UsableContours++;
                int n = contour.PointCount;
                facts.Points += n;
                var p = contour.Points;
                if (IsDegenerate(contour)) facts.DegenerateContours++;

                double zLo = double.MaxValue, zHi = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    for (int a = 0; a < 3; a++)
                    {
                        min[a] = Math.Min(min[a], p[i * 3 + a]);
                        max[a] = Math.Max(max[a], p[i * 3 + a]);
                    }
                    zLo = Math.Min(zLo, p[i * 3 + 2]);
                    zHi = Math.Max(zHi, p[i * 3 + 2]);
                }

                bool planar = IsPlanar(contour.GeometricType);
                double planeZ = (zLo + zHi) / 2;
                if (planar)
                    planes.Add((long)Math.Round(planeZ * 100));

                if (grid == null) continue;

                var fov = Fov(contour, grid);
                if (fov == FovRelation.Outside) { outside++; continue; }
                if (fov == FovRelation.Partly) partly++;
                if (!planar) continue;

                double distance = grid.NearestSlice(planeZ, out int slice);
                slices.Add(slice);
                if (zHi - zLo > OffPlaneToleranceMm || distance > OffPlaneToleranceMm)
                    offPlane++;
            }

            if (facts.UsableContours > 0)
            {
                facts.BoundsMinMm = ImageGeometry.Round(min);
                facts.BoundsMaxMm = ImageGeometry.Round(max);
                facts.ZMin = ImageGeometry.Round(min[2]);
                facts.ZMax = ImageGeometry.Round(max[2]);
                facts.ZExtentMm = ImageGeometry.Round(max[2] - min[2]);
            }
            facts.Planes = planes.Count;
            if (grid != null)
            {
                facts.SlicesWithContours = slices.Count;
                facts.OffPlaneContours = offPlane;
                facts.OutsideFovContours = outside;
                facts.PartlyOutsideFovContours = partly;
            }
            facts.Empty = facts.UsableContours == 0;
            facts.Degenerate = !facts.Empty && facts.DegenerateContours == facts.UsableContours;
            return facts;
        }

        internal static bool IsPlanar(string type) => type == "CLOSED_PLANAR" || type == "OPEN_PLANAR";

        /// <summary>
        /// A contour that marks no voxel as its type intends: a closed one of fewer than three
        /// points or (planar) no area; an open one of one point. POINT and unknown types never are.
        /// </summary>
        internal static bool IsDegenerate(ContourRecord contour)
        {
            int n = contour.PointCount;
            switch (contour.GeometricType)
            {
                case "CLOSED_PLANAR":
                    return n < 3 || PlanarArea(contour.Points, n) < ZeroAreaMm2;
                case "CLOSED_NONPLANAR":
                    return n < 3;
                case "OPEN_PLANAR":
                case "OPEN_NONPLANAR":
                    return n < 2;
                default:
                    return false;
            }
        }

        /// <summary>Area of a polygon's projection on the plane its normal is closest to (Newell's method), mm².</summary>
        private static double PlanarArea(double[] p, int n)
        {
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double xi = p[i * 3], yi = p[i * 3 + 1], zi = p[i * 3 + 2];
                double xj = p[j * 3], yj = p[j * 3 + 1], zj = p[j * 3 + 2];
                nx += (yi - yj) * (zi + zj);
                ny += (zi - zj) * (xi + xj);
                nz += (xi - xj) * (yi + yj);
            }
            return Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        }

        private enum FovRelation { Inside, Partly, Outside }

        /// <summary>
        /// Where a contour sits against the voxels: its bounding box in index space disjoint from
        /// [-0.5, n - 0.5] on some axis (in z, the slices' covered range) is outside; any point
        /// outside is partly; else inside.
        /// </summary>
        private static FovRelation Fov(ContourRecord contour, ImageGrid grid)
        {
            double cLo = double.MaxValue, cHi = double.MinValue, rLo = double.MaxValue, rHi = double.MinValue;
            double zLo = double.MaxValue, zHi = double.MinValue;
            bool anyOutside = false;
            double zLow = grid.ZLow, zHigh = grid.ZHigh;
            var p = contour.Points;
            for (int i = 0; i < contour.PointCount; i++)
            {
                double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                double c = grid.ColumnIndex(x, y, z), r = grid.RowIndex(x, y, z);
                cLo = Math.Min(cLo, c); cHi = Math.Max(cHi, c);
                rLo = Math.Min(rLo, r); rHi = Math.Max(rHi, r);
                zLo = Math.Min(zLo, z); zHi = Math.Max(zHi, z);
                if (c < -0.5 || c > grid.Columns - 0.5 || r < -0.5 || r > grid.Rows - 0.5
                    || z < zLow - OffPlaneToleranceMm || z > zHigh + OffPlaneToleranceMm)
                    anyOutside = true;
            }
            bool disjoint = double.IsNaN(cLo) || double.IsNaN(rLo)
                || cHi < -0.5 || cLo > grid.Columns - 0.5
                || rHi < -0.5 || rLo > grid.Rows - 0.5
                || zHi < zLow - OffPlaneToleranceMm || zLo > zHigh + OffPlaneToleranceMm;
            if (disjoint) return FovRelation.Outside;
            return anyOutside ? FovRelation.Partly : FovRelation.Inside;
        }
    }
}
