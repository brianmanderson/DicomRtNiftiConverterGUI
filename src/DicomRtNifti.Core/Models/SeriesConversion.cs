using System.Collections.Generic;

namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// What converting one image series wrote: the spacing of the grid and, when a structure set
    /// was converted with it, each exported ROI's volume.
    /// </summary>
    public class SeriesConversion
    {
        /// <summary>
        /// [x, y, z] spacing in mm: the resample target when the image was written resampled, the
        /// series' own otherwise.
        /// </summary>
        public double[] Spacing { get; set; }

        /// <summary>
        /// ROI output name to volume in cc, or null when no structure set was converted or it has
        /// no file. Same contents as <c>NiftiConversionService.ConvertStructToNifti</c> returns.
        /// </summary>
        public Dictionary<string, double> RoiVolumes { get; set; }
    }
}
