using System;
using System.Linq;
using Newtonsoft.Json;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// Where an image series' slices point, from its ImageOrientationPatient.
    ///
    /// Why it matters: the slice-spacing figures (<see cref="Services.SeriesGeometryProbe"/>) are
    /// gaps in ImagePositionPatient[2], and the rasterizer assigns a planar contour to the slice
    /// whose ImagePositionPatient[2] is nearest the contour's mean z (toolkit issue 44). Both are
    /// right only when the slice normal is the patient z axis. A tilted normal — gantry tilt, an
    /// oblique MR stack, a coronal or sagittal series — makes both measure the wrong axis, so this
    /// type reports the tilt and <see cref="FlagCodes.GeomOblique"/> says not to trust them. It
    /// fixes nothing.
    /// </summary>
    public sealed class SeriesOrientation
    {
        /// <summary>
        /// A direction within this many degrees of a patient axis counts as on it. At 0.1° a point
        /// 250 mm across the slice is 0.44 mm off the plane's z, under half the gap of any common
        /// reconstruction; the IOP rounding scanners write is far below it.
        /// </summary>
        public const double TiltToleranceDegrees = 0.1;

        public const string Axial = "axial";
        public const string Coronal = "coronal";
        public const string Sagittal = "sagittal";
        public const string Oblique = "oblique";

        private SeriesOrientation() { }

        /// <summary>Unit direction of increasing column index along a row (DICOM's first three values).</summary>
        public double[] RowCosines { get; private set; }

        /// <summary>Unit direction of increasing row index down a column (DICOM's last three values).</summary>
        public double[] ColumnCosines { get; private set; }

        /// <summary>Row × column: the direction the slices are stacked along.</summary>
        public double[] SliceNormal { get; private set; }

        /// <summary>
        /// <see cref="Axial"/>, <see cref="Coronal"/> or <see cref="Sagittal"/> when the normal is
        /// within <see cref="TiltToleranceDegrees"/> of the patient z, y or x axis; otherwise
        /// <see cref="Oblique"/>.
        /// </summary>
        public string Plane { get; private set; }

        /// <summary>
        /// True when both the row and the column direction lie on patient axes. An axial series
        /// rotated in-plane has an axial normal but is not axis-aligned.
        /// </summary>
        public bool AxisAligned { get; private set; }

        /// <summary>Angle in degrees between the slice normal and the patient z axis: 0 for axial, 90 for coronal or sagittal.</summary>
        public double NormalTiltDegrees { get; private set; }

        /// <summary>
        /// The slice normal is off the patient z axis by more than <see cref="TiltToleranceDegrees"/>:
        /// the predicate behind <see cref="FlagCodes.GeomOblique"/>, which the document carries instead.
        /// </summary>
        [JsonIgnore]
        public bool IsTilted => NormalTiltDegrees > TiltToleranceDegrees;

        /// <summary>
        /// Describes six direction cosines (row, then column). Null when there are fewer than six,
        /// or they do not span a plane (a zero or repeated direction).
        /// </summary>
        public static SeriesOrientation FromImageOrientation(double[] imageOrientationPatient)
        {
            if (imageOrientationPatient == null || imageOrientationPatient.Length < 6)
                return null;

            var row = Normalize(imageOrientationPatient.Take(3).ToArray());
            var column = Normalize(imageOrientationPatient.Skip(3).Take(3).ToArray());
            if (row == null || column == null)
                return null;

            var normal = Normalize(new[]
            {
                row[1] * column[2] - row[2] * column[1],
                row[2] * column[0] - row[0] * column[2],
                row[0] * column[1] - row[1] * column[0],
            });
            if (normal == null)
                return null;

            int axis = DominantAxis(normal);
            string plane = DegreesOffAxis(normal) > TiltToleranceDegrees
                ? Oblique
                : axis == 2 ? Axial : axis == 1 ? Coronal : Sagittal;

            return new SeriesOrientation
            {
                RowCosines = Round(row),
                ColumnCosines = Round(column),
                SliceNormal = Round(normal),
                Plane = plane,
                AxisAligned = DegreesOffAxis(row) <= TiltToleranceDegrees && DegreesOffAxis(column) <= TiltToleranceDegrees,
                NormalTiltDegrees = Math.Round(Degrees(Math.Abs(normal[2])), 6),
            };
        }

        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(v.Sum(x => x * x));
            if (!(length > 1e-6) || double.IsInfinity(length))
                return null;
            return v.Select(x => x / length).ToArray();
        }

        private static int DominantAxis(double[] unit)
        {
            int axis = 0;
            for (int i = 1; i < 3; i++)
            {
                if (Math.Abs(unit[i]) > Math.Abs(unit[axis])) axis = i;
            }
            return axis;
        }

        /// <summary>Degrees between a unit vector and the patient axis it is nearest.</summary>
        private static double DegreesOffAxis(double[] unit) => Degrees(Math.Abs(unit[DominantAxis(unit)]));

        /// <summary>The angle whose cosine is <paramref name="cosine"/>, in degrees, clamped against rounding past 1.</summary>
        private static double Degrees(double cosine) => Math.Acos(Math.Min(1.0, cosine)) * 180.0 / Math.PI;

        /// <summary>Six decimals: far below any tolerance here, and it keeps the last-bit noise of one platform's maths library out of the document.</summary>
        private static double[] Round(double[] v) => v.Select(x => Math.Round(x, 6) + 0.0).ToArray();
    }
}
