using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The reverse window's server (drop-folder) mode, driven tick by tick: the timer is pushed out
    /// of the way and each test calls the tick the timer would. A case is "dropped" by copying a
    /// NIfTI case folder (built from <see cref="TestCase"/> through the Core forward services) into
    /// the watched folder.
    /// </summary>
    public class ReverseServerModeTests
    {
        private readonly ITestOutputHelper _out;

        public ReverseServerModeTests(ITestOutputHelper output) => _out = output;

        [AvaloniaFact]
        public async Task Stop_cancels_a_server_job_mid_run_and_the_server_does_not_retry_it()
        {
            using (new UserSettingsGuard())
            using (var testCase = new TestCase())
            using (var launcher = new LauncherSession())
            {
                string staged = Path.Combine(testCase.Root, "staged");
                await NiftiCase.WriteAsync(testCase, launcher.Services, staged);
                string watched = Path.Combine(testCase.Root, "watched");
                Directory.CreateDirectory(watched);

                var window = launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
                vm.ServerInterval = TimeSpan.FromHours(1); // the test runs the ticks
                vm.RootFolder = watched;
                vm.ConvertImage = true;
                vm.ConvertStructures = true;
                vm.ConvertDoses = false;

                Assert.True(vm.RunServerCommand.CanExecute(null));
                vm.RunServerCommand.Execute(null);
                await vm.LastServerTick;
                Assert.True(vm.IsServerMode);
                Assert.Equal("Stop Server", vm.ServerButtonText);
                Assert.Empty(vm.DiscoveredJobs);

                // Drop a case. First sight: fingerprinted, waiting for the upload to settle.
                string caseFolder = Path.Combine(watched, "case01");
                NiftiCase.CopyTo(staged, caseFolder);
                await vm.RunServerTickAsync();
                var job = Assert.Single(vm.DiscoveredJobs);
                Assert.StartsWith("Watching", job.Status);
                Assert.False(vm.IsBusy);

                // Press Stop at the job's first progress report, i.e. while it is converting.
                bool stopPressed = false, stopWasEnabled = false, serverToggleWasEnabled = false;
                vm.PropertyChanged += (_, e) =>
                {
                    if (stopPressed || e.PropertyName != nameof(NiftiToDicomViewModel.StatusText)) return;
                    if (!vm.IsBusy || !vm.StatusText.StartsWith("case01: ", StringComparison.Ordinal)) return;
                    stopPressed = true;
                    stopWasEnabled = vm.CancelCommand.CanExecute(null);
                    serverToggleWasEnabled = vm.RunServerCommand.CanExecute(null);
                    _out.WriteLine($"Stop pressed at: {vm.StatusText} (job status: {job.Status})");
                    vm.CancelCommand.Execute(null);
                };

                await vm.RunServerTickAsync(); // settled: the job runs, and Stop lands mid-run
                Dispatcher.UIThread.RunJobs();
                _out.WriteLine(vm.LogText);

                Assert.True(stopPressed, "the job never reported progress while the tick was busy");
                Assert.True(stopWasEnabled, "Stop was greyed out while a server job ran");
                Assert.True(serverToggleWasEnabled, "Stop Server was greyed out while a server job ran");
                Assert.StartsWith("Cancelled", job.Status);
                Assert.Empty(Directory.GetFiles(caseFolder, "RTSTRUCT_*.dcm"));
                Assert.False(vm.IsBusy);
                Assert.True(vm.IsServerMode, "Stop cancels the job, not the server");
                Assert.Contains("Stop cancelled the job in progress", vm.LogText);
                Assert.Contains("case01: Cancelled", vm.LogText);

                // Nothing about the case changed, so the server leaves it cancelled.
                await vm.RunServerTickAsync();
                await vm.RunServerTickAsync();
                Assert.StartsWith("Cancelled", job.Status);
                Assert.Empty(Directory.GetFiles(caseFolder, "RTSTRUCT_*.dcm"));
                Assert.Contains("1 cancelled, left alone until their files change", vm.StatusText);
                Assert.DoesNotContain("waiting for stability", vm.StatusText);

                Screenshots.Capture(window, "11_nifti_to_dicom_server_stopped_job");

                vm.RunServerCommand.Execute(null);
                Assert.False(vm.IsServerMode);
                Assert.Equal("Server stopped.", vm.StatusText);
            }
        }

        [AvaloniaFact]
        public async Task Server_rebuilds_the_rtstruct_when_a_mask_is_overwritten_in_place()
        {
            using (new UserSettingsGuard())
            using (var testCase = new TestCase())
            using (var launcher = new LauncherSession())
            {
                string watched = Path.Combine(testCase.Root, "watched");
                string caseFolder = Path.Combine(watched, "case01");
                await NiftiCase.WriteAsync(testCase, launcher.Services, caseFolder);

                launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
                vm.ServerInterval = TimeSpan.FromHours(1);
                vm.RootFolder = watched;
                vm.ConvertImage = true;
                vm.ConvertStructures = true;
                vm.ConvertDoses = false;

                vm.RunServerCommand.Execute(null);
                await vm.LastServerTick;                       // first sight
                var job = Assert.Single(vm.DiscoveredJobs);
                await vm.RunServerTickAsync();                 // settled: converted
                Assert.StartsWith("Converted", job.Status);

                string rtstruct = Path.Combine(caseFolder, HashNaming.RtStructFileName(job.MaskBaseNames));
                Assert.True(File.Exists(rtstruct), $"server mode did not write {Path.GetFileName(rtstruct)}");

                await SettleAsync(vm);
                Assert.StartsWith("Up-to-date", job.Status);
                Assert.Contains("masks up-to-date", job.Status);

                // A corrected prediction lands under the same name: same file name, newer content.
                DateTime built = File.GetLastWriteTimeUtc(rtstruct);
                string mask = Directory.GetFiles(Path.Combine(caseFolder, "masks"), "*.nii.gz").First();
                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(mask, now > built ? now : built.AddMilliseconds(1));

                await SettleAsync(vm);
                _out.WriteLine(vm.LogText);
                Assert.StartsWith("Converted", job.Status);
                Assert.Contains("RT-STRUCT rebuilt, a mask was newer", job.Status);
                Assert.True(File.GetLastWriteTimeUtc(rtstruct) > built, "the RT-STRUCT was not rewritten");
                Assert.Single(Directory.GetFiles(caseFolder, "RTSTRUCT_*.dcm"));

                // And the rule settles: the rebuilt structure set is newer than every mask.
                await SettleAsync(vm);
                Assert.StartsWith("Up-to-date", job.Status);

                vm.RunServerCommand.Execute(null);
            }
        }

        /// <summary>Two ticks: the first sees the folder change, the second finds it settled and runs it.</summary>
        private static async Task SettleAsync(NiftiToDicomViewModel vm)
        {
            await vm.RunServerTickAsync();
            await vm.RunServerTickAsync();
            Dispatcher.UIThread.RunJobs();
        }

        [Fact]
        public void An_output_is_stale_only_when_an_input_was_written_after_it()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string input = Path.Combine(dir, "PTV.nii.gz");
                string output = Path.Combine(dir, "RTSTRUCT_x.dcm");
                File.WriteAllText(input, "mask");

                Assert.Equal(NiftiToDicomViewModel.OutputState.Missing, NiftiToDicomViewModel.CompareToOutput(new[] { input }, output));

                File.WriteAllText(output, "rtstruct");
                var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(output, t);
                File.SetLastWriteTimeUtc(input, t.AddSeconds(-1));
                Assert.Equal(NiftiToDicomViewModel.OutputState.UpToDate, NiftiToDicomViewModel.CompareToOutput(new[] { input }, output));

                File.SetLastWriteTimeUtc(input, t); // same instant: not newer
                Assert.Equal(NiftiToDicomViewModel.OutputState.UpToDate, NiftiToDicomViewModel.CompareToOutput(new[] { input }, output));

                File.SetLastWriteTimeUtc(input, t.AddSeconds(1));
                Assert.Equal(NiftiToDicomViewModel.OutputState.Stale, NiftiToDicomViewModel.CompareToOutput(new[] { input }, output));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Error_text_carries_every_inner_message_once()
        {
            var ex = new InvalidOperationException("RT-STRUCT write failed",
                new IOException("could not read PTV.nii.gz",
                    new UnauthorizedAccessException("access denied")));
            Assert.Equal("RT-STRUCT write failed → could not read PTV.nii.gz → access denied",
                NiftiToDicomViewModel.DescribeError(ex));

            var wrapped = new AggregateException(new IOException("disk full"));
            Assert.Equal("disk full", NiftiToDicomViewModel.DescribeError(wrapped));

            var several = new AggregateException(new IOException("a"), new IOException("b"));
            Assert.Equal("a → b", NiftiToDicomViewModel.DescribeError(several));
        }
    }
}
