using System;
using System.IO;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>Locates the repository from the test output directory.</summary>
    internal static class RepoPaths
    {
        public static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "DicomRtNifti.sln")))
                        return dir.FullName;
                }

                throw new InvalidOperationException("DicomRtNifti.sln not found above the test output directory.");
            }
        }

        public static string WebProject => Path.Combine(Root, "src", "DicomRtNifti.Web");
    }
}
