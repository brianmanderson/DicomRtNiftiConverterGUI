using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// Image geometry from the scan model: one series through <see cref="SeriesGeometryProbe"/>,
    /// and the cohort as distributions of the measures a curator picks an output spacing by.
    /// </summary>
    public static class ImageGeometry
    {
        /// <summary>The grid facts of one image series.</summary>
        public static SeriesGeometryFacts Describe(DicomSeriesGroup series)
        {
            if (series == null) throw new ArgumentNullException(nameof(series));

            var facts = new SeriesGeometryFacts
            {
                InstanceCount = series.FilePaths?.Count ?? 0,
                SliceCount = series.SlicePositions?.Count ?? 0,
                UnpositionedSlices = series.UnpositionedSlices?.Count ?? 0,
                PixelSpacing = series.PixelSpacing != null && series.PixelSpacing.Length >= 2
                    ? Round(series.PixelSpacing.Take(2))
                    : null,
                SliceThickness = Round(series.SliceThickness),
                Rows = series.Rows,
                Columns = series.Columns,
            };

            if (SeriesGeometryProbe.TryDescribe(series, out var geometry))
            {
                facts.Spacing = geometry.Spacing == null ? null : Round(geometry.Spacing);
                facts.SliceSpacing = Round(geometry.SliceSpacing);
                facts.SliceSpacingUniform = geometry.Uniform;
                facts.SliceSpacingValues = Round(geometry.DistinctGaps);
                facts.ZMin = Round(series.SlicePositions.Min());
                facts.ZMax = Round(series.SlicePositions.Max());
                facts.ExtentMm = Round(geometry.ExtentMm);

                // The factor SeriesGeometryProbe gates its warning on, computed the same way: the
                // written spacing is the endpoint average a regular-grid reader flattens to.
                int count = series.SlicePositions.Count;
                if (count >= 2 && geometry.DistinctGaps.Length > 0)
                {
                    double written = geometry.ExtentMm / (count - 1);
                    var gaps = geometry.DistinctGaps;
                    facts.SpacingErrorFactor = Round(
                        SeriesGeometryProbe.SpacingErrorFactor(gaps[0], gaps[gaps.Length - 1], written));
                }
            }

            if (facts.PixelSpacing != null && series.Rows.HasValue && series.Columns.HasValue)
            {
                facts.FieldOfViewMm = Round(new[]
                {
                    series.Columns.Value * series.PixelSpacing[1],
                    series.Rows.Value * series.PixelSpacing[0],
                });
            }

            return facts;
        }

        /// <summary>The cohort's geometry, from the analysed image series.</summary>
        public static GeometrySummary Summarize(IReadOnlyList<SeriesAnalysis> series)
        {
            var summary = new GeometrySummary { ImageSeries = series.Count };
            foreach (var plane in new[] { SeriesOrientation.Axial, SeriesOrientation.Coronal, SeriesOrientation.Sagittal, SeriesOrientation.Oblique, "unknown" })
                summary.Planes[plane] = 0;

            foreach (var s in series)
            {
                var g = s.Geometry;
                if (g.SliceCount > 0) summary.WithPositions++;
                bool material = s.Flags.Contains(FlagCodes.GeomNonUniformZ);
                if (g.SliceSpacingUniform == true) summary.Uniform++;
                else if (g.SliceSpacingUniform == false && !material) summary.NonUniformWithinTolerance++;
                if (material) summary.NonUniformMaterial++;
                if (g.UnpositionedSlices > 0) summary.WithUnpositionedSlices++;
                summary.Planes[s.Orientation?.Plane ?? "unknown"]++;
            }

            summary.PixelSpacingXMm = Distribute(series.Where(s => s.Geometry.PixelSpacing != null).Select(s => s.Geometry.PixelSpacing[1]));
            summary.PixelSpacingYMm = Distribute(series.Where(s => s.Geometry.PixelSpacing != null).Select(s => s.Geometry.PixelSpacing[0]));
            summary.SliceSpacingMm = Distribute(series.Where(s => s.Geometry.SliceSpacing.HasValue).Select(s => s.Geometry.SliceSpacing.Value));
            summary.SliceCount = Distribute(series.Where(s => s.Geometry.SliceCount > 0).Select(s => (double)s.Geometry.SliceCount));
            summary.ExtentZMm = Distribute(series.Where(s => s.Geometry.SliceCount >= 2 && s.Geometry.ExtentMm.HasValue).Select(s => s.Geometry.ExtentMm.Value));

            summary.Grids = series
                .Where(s => s.Geometry.Rows.HasValue || s.Geometry.Columns.HasValue)
                .GroupBy(s => (s.Geometry.Rows, s.Geometry.Columns))
                .Select(grp => new GridCount { Rows = grp.Key.Rows, Columns = grp.Key.Columns, Series = grp.Count() })
                .OrderByDescending(grid => grid.Series)
                .ThenBy(grid => grid.Rows ?? -1)
                .ThenBy(grid => grid.Columns ?? -1)
                .ToList();

            return summary;
        }

        /// <summary>Range, median and value counts (to 4 decimals) of one measure.</summary>
        internal static Distribution Distribute(IEnumerable<double> values)
        {
            var sorted = values.Select(v => Math.Round(v, 6)).OrderBy(v => v).ToList();
            var distribution = new Distribution { Series = sorted.Count };
            if (sorted.Count == 0)
                return distribution;

            int mid = sorted.Count / 2;
            distribution.Min = sorted[0];
            distribution.Max = sorted[sorted.Count - 1];
            distribution.Median = Math.Round(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0, 6);
            distribution.Values = sorted
                .GroupBy(v => Math.Round(v, 4) + 0.0)
                .OrderBy(grp => grp.Key)
                .Select(grp => new ValueCount { Value = grp.Key, Series = grp.Count() })
                .ToList();
            return distribution;
        }

        /// <summary>
        /// Six decimals, and never negative zero: a derived number carries the last-bit noise of the
        /// arithmetic that made it, which would otherwise differ between platforms in the document.
        /// </summary>
        internal static double Round(double value) => Math.Round(value, 6) + 0.0;

        internal static double? Round(double? value) => value.HasValue ? Round(value.Value) : (double?)null;

        internal static double[] Round(IEnumerable<double> values) => values?.Select(v => Round(v)).ToArray();
    }
}
