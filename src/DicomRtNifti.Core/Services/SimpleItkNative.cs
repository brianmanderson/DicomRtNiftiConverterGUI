using System;
using itk.simple;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Whether the per-platform SimpleITK native (SimpleITKCSharpNative) resolved on this machine.
    /// It is not a NuGet package but a DLL staged beside the binaries, so a missing or wrong-arch
    /// copy only shows up when the first image is touched; every front-end can ask here first
    /// (the CLI's --version prints <see cref="Probe"/>).
    /// </summary>
    public static class SimpleItkNative
    {
        /// <summary>
        /// Load-probes the native by constructing a 1x1x1 image, which forces it into memory
        /// without needing any input files. Returns "SimpleITK native: OK (...)" or
        /// "SimpleITK native: FAILED TO LOAD -- ..." with the whole inner-exception chain.
        /// </summary>
        public static string Probe()
        {
            TryProbe(out string status);
            return status;
        }

        /// <summary>As <see cref="Probe"/>, also saying whether the native loaded.</summary>
        public static bool TryProbe(out string status)
        {
            try
            {
                using (var img = new Image(1, 1, 1, PixelIDValueEnum.sitkUInt8))
                {
                    status = $"SimpleITK native: OK ({img.GetNumberOfPixels()}-voxel probe)";
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Walk the inner-exception chain: the outer TypeInitializationException
                // hides the real dlopen / DllNotFound detail that pins down a native load
                // failure (missing dependency, code-signature rejection, arch mismatch).
                string detail = "";
                for (var e = ex; e != null; e = e.InnerException)
                    detail += (detail.Length > 0 ? " <- " : "") + e.GetType().Name + ": " + e.Message;
                status = "SimpleITK native: FAILED TO LOAD -- " + detail;
                return false;
            }
        }
    }
}
