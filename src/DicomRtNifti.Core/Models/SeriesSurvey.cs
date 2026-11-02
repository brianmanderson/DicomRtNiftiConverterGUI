using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// What the manifest-only survey reads from one image series: the spacing of its reference
    /// grid and, when a structure set was surveyed with it, each exported ROI's volume.
    /// </summary>
    public class SeriesSurvey
    {
        /// <summary>[x, y, z] spacing in mm of the series as loaded, before any resampling.</summary>
        public double[] Spacing { get; set; }

        /// <summary>
        /// ROI output name to volume in cc, or null when no structure set was surveyed or it has no
        /// file. Same contents and order as <c>NiftiConversionService.ComputeStructVolumes</c>.
        /// </summary>
        public Dictionary<string, double> RoiVolumes { get; set; }
    }
}
