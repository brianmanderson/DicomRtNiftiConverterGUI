using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// A reverse-direction case folder (image.nii.gz + masks/*.nii.gz) built from a
    /// <see cref="TestCase"/>'s CT and RTSTRUCT through the same Core services the forward window
    /// calls, without opening it. Written once to a staging folder, then copied wherever a test
    /// "drops" a case.
    /// </summary>
    public static class NiftiCase
    {
        public static async Task WriteAsync(TestCase testCase, TestServices services, string folder)
        {
            Directory.CreateDirectory(folder);
            var scan = await services.Scanner.ScanFolderAsync(testCase.InputFolder, null, CancellationToken.None);
            var series = scan.Patients.SelectMany(p => p.Studies).SelectMany(s => s.Series)
                .Single(s => s.Modality == "CT");

            services.Conversion.ConvertImageSeriesToNifti(series, folder, null, CancellationToken.None);
            services.Conversion.ConvertStructToNifti(series.LinkedRtStruct, series, folder,
                new List<RoiAssociation>(), true, false, null, CancellationToken.None);
        }

        /// <summary>Copies a case folder, subfolders included, as a user dropping it in would.</summary>
        public static void CopyTo(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(source))
                CopyTo(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
