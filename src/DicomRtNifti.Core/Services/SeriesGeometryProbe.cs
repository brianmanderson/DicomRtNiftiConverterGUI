using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Summarises a series' voxel geometry from the headers the scanner already read.
    ///
    /// The uniformity flag is the point of this type. A CT reconstructed with mixed slice gaps
    /// — 3 mm through the region of interest, 6 mm elsewhere — reads back a single averaged
    /// spacing from any reader that assumes a regular grid, which silently shifts contour planes
    /// during rasterization. Surfacing "this series is not uniform in z" in a cohort survey lets
    /// that be caught before it becomes a training-set defect.
    /// </summary>
    public static class SeriesGeometryProbe
    {
        /// <summary>Slice gaps within this many mm of each other count as the same gap.</summary>
        private const double GapTolerance = 1e-3;

        /// <summary>
        /// Describes <paramref name="series"/>. Returns false when there is no geometry to
        /// report — an RTSTRUCT or RTDOSE series, or an image series whose slices carried no
        /// ImagePositionPatient.
        /// </summary>
        public static bool TryDescribe(DicomSeriesGroup series, out SeriesGeometry geometry)
        {
            geometry = null;
            if (series == null || series.SlicePositions == null || series.SlicePositions.Count == 0)
                return false;

            var positions = series.SlicePositions.OrderBy(z => z).ToList();

            geometry = new SeriesGeometry
            {
                InstanceCount = series.FilePaths?.Count ?? positions.Count,
                PixelSpacing = series.PixelSpacing,
            };

            if (positions.Count == 1)
            {
                // Nothing to difference. SliceThickness is the only through-plane number
                // available, and a single slice is trivially uniform.
                geometry.SliceSpacing = series.SliceThickness;
                geometry.Uniform = true;
                geometry.DistinctGaps = new double[0];
                return true;
            }

            var gaps = new List<double>(positions.Count - 1);
            for (int i = 1; i < positions.Count; i++)
                gaps.Add(Math.Round(positions[i] - positions[i - 1], 6));

            var distinct = new List<double>();
            foreach (var gap in gaps)
            {
                if (!distinct.Any(d => Math.Abs(d - gap) <= GapTolerance))
                    distinct.Add(gap);
            }
            distinct.Sort();

            geometry.DistinctGaps = distinct.ToArray();
            geometry.Uniform = distinct.Count == 1;
            geometry.SliceSpacing = Median(gaps);
            geometry.ExtentMm = positions[positions.Count - 1] - positions[0];

            return true;
        }

        /// <summary>
        /// The median gap rather than the mean: with mixed 3 mm / 6 mm reconstructions the mean
        /// reports a spacing that no pair of slices actually has.
        /// </summary>
        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }

    /// <summary>Voxel geometry of one image series, derived from scanned headers.</summary>
    public class SeriesGeometry
    {
        /// <summary>In-plane spacing [row, column] in mm, or null when the tag was absent.</summary>
        public double[] PixelSpacing { get; set; }

        /// <summary>
        /// Median gap between consecutive slices in mm. Null only when a single-slice series
        /// also lacked SliceThickness.
        /// </summary>
        public double? SliceSpacing { get; set; }

        /// <summary>True when every consecutive slice gap is the same.</summary>
        public bool Uniform { get; set; }

        /// <summary>
        /// The distinct slice gaps present, ascending. A single entry means a regular grid;
        /// more than one names exactly which gaps are mixed.
        /// </summary>
        public double[] DistinctGaps { get; set; } = new double[0];

        /// <summary>Number of files in the series.</summary>
        public int InstanceCount { get; set; }

        /// <summary>Distance in mm from the first slice to the last.</summary>
        public double ExtentMm { get; set; }

        /// <summary>Full [x, y, z] spacing, or null when either component is unknown.</summary>
        public double[] Spacing =>
            (PixelSpacing != null && PixelSpacing.Length >= 2 && SliceSpacing.HasValue)
                ? new[] { PixelSpacing[0], PixelSpacing[1], SliceSpacing.Value }
                : null;
    }
}
