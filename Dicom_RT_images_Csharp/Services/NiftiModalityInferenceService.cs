using System;
using System.Runtime.InteropServices;
using itk.simple;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Heuristic CT / MR / PT modality inference from a NIfTI image.
    ///
    /// <para>
    /// The image-reverse pipeline (<see cref="NiftiImageWriterService"/>) was
    /// originally CT-only: integer storage with <c>RescaleSlope=1</c>,
    /// <c>RescaleIntercept=0</c>, and a hard-coded <c>RescaleType="HU"</c>.
    /// Generalising that to MR / PT requires choosing the right SOP class,
    /// pixel signedness, adaptive rescale, and modality-specific tags
    /// (Units / CorrectedImage for PT, KVP / RescaleType=HU for CT). The
    /// caller can pass <c>--modality CT|MR|PT</c> explicitly; this service
    /// supplies a reasonable default when the caller hasn't decided.
    /// </para>
    ///
    /// <para>Heuristics (in order of priority):</para>
    /// <list type="bullet">
    /// <item>
    /// <description>If the sampled minimum is &lt; -200, return <b>CT</b>.
    /// HU air is -1000 and even partially-cropped CTs retain large negative
    /// regions; MR / PT volumes virtually never carry sub-zero values.
    /// </description>
    /// </item>
    /// <item>
    /// <description>Else, if the stored pixel type is an integer format AND
    /// the sampled minimum is &lt; 0, return <b>CT</b>. This catches
    /// soft-tissue-only / cropped CT volumes that never see HU air -- their
    /// dynamic range can sit entirely in roughly [-50, +200] HU but they
    /// still carry small negative values, and they remain integer-typed.
    /// MR data is essentially always non-negative integer; PT is float.
    /// </description>
    /// </item>
    /// <item>
    /// <description>Else, if the stored pixel type is floating-point AND the
    /// sampled max is &lt; 100, return <b>PT</b>. Clinical PT phantoms /
    /// reconstructions are float32 SUV-like values in [0, ~20]; a small
    /// positive dynamic range with a floating-point dtype is the
    /// fingerprint.
    /// </description>
    /// </item>
    /// <item>
    /// <description>Else return <b>MR</b>. MR data is typically uint16 or
    /// int16 with all-positive values and a large dynamic range; that's
    /// the natural fallback once CT and PT have been ruled out.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// Inference samples up to ~100K voxels uniformly across the volume so
    /// the cost stays bounded on a 512x512x500 CT (~131M voxels). Smaller
    /// volumes are scanned in full.
    /// </para>
    /// </summary>
    public static class NiftiModalityInferenceService
    {
        /// <summary>Maximum number of voxels examined to bound runtime.</summary>
        private const int MaxSampleVoxels = 100_000;

        /// <summary>Below this, assume HU air → CT.</summary>
        private const double CtMinValueThreshold = -200.0;

        /// <summary>Above this, the float volume is not a PT SUV image.</summary>
        private const double PtMaxValueThreshold = 100.0;

        /// <summary>
        /// Infer "CT" / "MR" / "PT" from <paramref name="image"/>.
        /// Throws <see cref="ArgumentNullException"/> when <paramref name="image"/>
        /// is null. Caller is responsible for the image's lifetime.
        /// </summary>
        public static string Infer(Image image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            PixelIDValueEnum originalPixelId = image.GetPixelID();
            bool isFloatingPoint =
                originalPixelId == PixelIDValueEnum.sitkFloat32 ||
                originalPixelId == PixelIDValueEnum.sitkFloat64;
            bool isIntegerPixel = !isFloatingPoint;

            (double sampleMin, double sampleMax) = SampleMinMax(image);

            // Strong CT signal: HU air (~-1000).
            if (sampleMin < CtMinValueThreshold)
            {
                return "CT";
            }
            // Weaker CT signal: integer storage with any negative values.
            // Catches soft-tissue-only / cropped CT volumes that never see
            // HU air but still keep small negative values in HU. MR is
            // virtually always non-negative; PT is float.
            if (isIntegerPixel && sampleMin < 0.0)
            {
                return "CT";
            }
            if (isFloatingPoint && sampleMax < PtMaxValueThreshold)
            {
                return "PT";
            }
            return "MR";
        }

        /// <summary>
        /// Sample up to <see cref="MaxSampleVoxels"/> voxels (uniform stride)
        /// and return their (min, max). Casts to float32 first so the buffer
        /// can be memcpy'd into managed memory regardless of source dtype.
        /// </summary>
        private static (double min, double max) SampleMinMax(Image image)
        {
            var size = image.GetSize();
            long total = 1L;
            for (int d = 0; d < size.Count; d++) total *= (long)size[d];
            if (total <= 0)
            {
                return (0.0, 0.0);
            }
            if (total > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Image too large for buffer copy ({total} voxels).");
            }

            Image f32 = (image.GetPixelID() == PixelIDValueEnum.sitkFloat32)
                ? image
                : SimpleITK.Cast(image, PixelIDValueEnum.sitkFloat32);
            bool ownsCast = !ReferenceEquals(f32, image);
            try
            {
                int totalInt = (int)total;
                float[] buf = new float[totalInt];
                IntPtr ptr = f32.GetBufferAsFloat();
                Marshal.Copy(ptr, buf, 0, totalInt);

                // Stride sampling: scan every (total / MaxSampleVoxels) voxel.
                int stride = Math.Max(1, totalInt / MaxSampleVoxels);
                double sMin = double.PositiveInfinity;
                double sMax = double.NegativeInfinity;
                for (int i = 0; i < totalInt; i += stride)
                {
                    double v = buf[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v < sMin) sMin = v;
                    if (v > sMax) sMax = v;
                }
                if (double.IsPositiveInfinity(sMin))
                {
                    // Image was entirely NaN / Inf — fall back to 0.
                    return (0.0, 0.0);
                }
                return (sMin, sMax);
            }
            finally
            {
                if (ownsCast) f32.Dispose();
            }
        }
    }
}
