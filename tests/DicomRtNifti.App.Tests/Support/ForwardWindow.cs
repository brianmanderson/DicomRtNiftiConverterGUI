using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// DICOM → NIfTI opened from the launcher with every export control at a known value, so a
    /// stored preference can never change what a test exports, then scanned with every patient
    /// ticked.
    /// </summary>
    public static class ForwardWindow
    {
        public static async Task<(DicomToNiftiWindow Window, MainViewModel Vm)> OpenScannedAsync(
            LauncherSession launcher, string input, string output)
        {
            var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
            vm.InputFolder = input;
            vm.OutputFolder = output;
            vm.ExportImages = true;
            vm.IncludeStructures = true;
            vm.IncludeDose = true;
            vm.OnlyExportSpecificRois = false;
            vm.SpecifyOutputSpacing = false;
            vm.AnonymizeExport = false;
            vm.AnonymizationKeyPath = "";
            vm.ExportDicomMetadata = false;
            vm.RequireStructures = false;
            vm.RequireDose = false;
            vm.SeriesDescriptionFilter = "";
            vm.StructDescriptionFilter = "";

            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Scan complete.", vm.StatusText);
            vm.AllPatientsSelected = true;
            return (window, vm);
        }
    }

    /// <summary>A temp folder for one test's input and output, deleted with it.</summary>
    public sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DicomRtNifti.App.Tests",
                Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>A subfolder of the root, created.</summary>
        public string Folder(string name)
        {
            string folder = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a straggling handle on Windows; the folder is per-test anyway */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
