using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// How far the flattened spacing has to be from the true gaps before the warning is worth
        /// printing, as a volume-scaling factor.
        ///
        /// <see cref="GapTolerance"/> is an absolute 1e-3 mm, which is the right test for "are
        /// these the same gap" only on a grid stored with enough decimals. A 0.625 mm
        /// reconstruction written to two decimals alternates 0.62 / 0.63 and a 3 mm series with a
        /// few microns of jitter differs in the sixth decimal; both are non-uniform by that test
        /// and both used to produce a warning that computed its own refutation — "off by up to
        /// 1.01x", "off by up to 1x". Reporting a 1% discrepancy in the same words as a 3.7x one
        /// is how a real finding gets ignored, so the factor the message already computes is now
        /// also the gate. 5% is comfortably above rounding and jitter and far below any mixed-gap
        /// reconstruction worth flagging.
        /// </summary>
        internal const double MaterialSpacingFactor = 1.05;

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
        /// Builds the warning a conversion should emit for a series whose slice gaps are mixed,
        /// or returns false when the series is uniform (or carries no positions to judge by).
        ///
        /// NIfTI stores one spacing per axis, so a mixed-gap series is flattened to a single
        /// number on the way out — the ImageSeriesReader's endpoint average, which is not any gap
        /// the scan actually has. The mask geometry is still rasterized against the true per-slice
        /// positions, but every volume derived from the written spacing is scaled by
        /// averageGap/trueGap. That is a silent multiple-fold error on a number people publish, so
        /// it has to be said out loud even though the conversion itself succeeds.
        /// </summary>
        /// <param name="warning">The message to log, or null when there is nothing to warn about.</param>
        /// <returns>True when <paramref name="warning"/> was set.</returns>
        public static bool TryBuildNonUniformSpacingWarning(DicomSeriesGroup series, out string warning)
        {
            return TryBuildNonUniformSpacingWarning(series, null, out warning);
        }

        /// <summary>
        /// As <see cref="TryBuildNonUniformSpacingWarning(DicomSeriesGroup, out string)"/>, for a
        /// conversion that resamples to <paramref name="targetSpacing"/>.
        ///
        /// The flattening still happens — the series is read on its own averaged grid and then
        /// resampled off it — so the warning stands, but the spacing it quotes is not what lands
        /// in the file. Saying "the output will be written at 11.2 mm" when the output is 1 mm
        /// isotropic sends the reader looking for a discrepancy that is not in the header.
        /// </summary>
        /// <param name="targetSpacing">Resample target in mm, or null when not resampling.</param>
        public static bool TryBuildNonUniformSpacingWarning(
            DicomSeriesGroup series, double[] targetSpacing, out string warning)
        {
            warning = null;

            SeriesGeometry geometry;
            if (!TryDescribe(series, out geometry) || geometry.Uniform)
                return false;

            var gaps = geometry.DistinctGaps;
            if (gaps == null || gaps.Length < 2)
                return false;

            // TryDescribe only reports non-uniform when it differenced at least two positions,
            // so the divisor below is always >= 1.
            int sliceCount = series.SlicePositions.Count;
            double writtenSpacing = geometry.ExtentMm / (sliceCount - 1);
            double errorFactor = SpacingErrorFactor(gaps[0], gaps[gaps.Length - 1], writtenSpacing);

            // Non-uniform by the absolute gap test, but the flattened spacing reproduces every
            // true gap to within rounding. Nothing to act on; see MaterialSpacingFactor.
            if (errorFactor < MaterialSpacingFactor)
                return false;

            warning = BuildNonUniformSpacingMessage(
                series.SeriesInstanceUID,
                sliceCount,
                gaps[0],
                gaps[gaps.Length - 1],
                writtenSpacing,
                errorFactor,
                targetSpacing);
            return true;
        }

        /// <summary>
        /// Worst-case factor by which a volume computed from <paramref name="writtenSpacing"/> is
        /// wrong. Both directions count: the flattened spacing over-states thin gaps by
        /// written/min and under-states wide ones by max/written, and "off by up to" means the
        /// larger of the two.
        /// </summary>
        internal static double SpacingErrorFactor(double minGap, double maxGap, double writtenSpacing)
        {
            double worst = 1.0;
            if (writtenSpacing <= 0)
                return worst;
            if (minGap > 0)
                worst = Math.Max(worst, writtenSpacing / minGap);
            if (maxGap > 0)
                worst = Math.Max(worst, maxGap / writtenSpacing);
            return worst;
        }

        /// <summary>
        /// Wording for <see cref="TryBuildNonUniformSpacingWarning(DicomSeriesGroup, out string)"/>.
        /// Internal and static so the numbers in it are unit-tested without a DICOM tree. Formatted
        /// invariantly: this goes to stderr, which harnesses parse.
        /// </summary>
        internal static string BuildNonUniformSpacingMessage(
            string seriesInstanceUid, int sliceCount, double minGap, double maxGap,
            double writtenSpacing, double errorFactor, double[] targetSpacing)
        {
            string uid = string.IsNullOrEmpty(seriesInstanceUid) ? "(unknown UID)" : seriesInstanceUid;

            string head = string.Format(
                CultureInfo.InvariantCulture,
                "WARNING: series {0} has non-uniform slice spacing — {1} slices with gaps from " +
                "{2:0.###} mm to {3:0.###} mm. ",
                uid, sliceCount, minGap, maxGap);

            string body = targetSpacing == null
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "NIfTI carries one spacing per axis, so the output will be written at " +
                    "{0:0.###} mm throughout and any volume computed from it will be off by up " +
                    "to {1:0.##}x. ",
                    writtenSpacing, errorFactor)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "NIfTI carries one spacing per axis, so the series is flattened to " +
                    "{0:0.###} mm and then resampled: the output will be written at the requested " +
                    "{1} mm, not at {0:0.###} mm. Resampling from a flattened grid does not " +
                    "recover the true slice positions, so any volume computed from the output " +
                    "will still be off by up to {2:0.##}x. ",
                    writtenSpacing, FormatSpacing(targetSpacing), errorFactor);

            return head + body +
                "Resample the source series to a uniform grid before converting if the geometry matters.";
        }

        /// <summary>Renders a resample target as "XxYxZ", matching the CLI's own echo of it.</summary>
        private static string FormatSpacing(double[] spacing)
        {
            if (spacing == null || spacing.Length == 0)
                return "(unknown)";
            return string.Join("x", spacing.Select(
                v => v.ToString("0.###", CultureInfo.InvariantCulture)));
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
