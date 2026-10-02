using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// The launcher as the App composes it: the version footer, and the two workflow buttons
    /// opening the right windows with the right view-models.
    /// </summary>
    public class LauncherWindowTests
    {
        [AvaloniaFact]
        public void Footer_shows_the_informational_version_with_the_commit_sha()
        {
            using (var launcher = new LauncherSession())
            {
                string text = launcher.Window.FindControl<TextBlock>("VersionText").Text;

                // 1.0.0+<sha7>: the same AssemblyInformationalVersion the CLI prints with --version.
                // A bare "1.0.0" here means the build lost its source revision (Directory.Build.props
                // refuses that build, so this guards the footer code itself).
                Assert.Matches(@"^DICOM RT Toolkit  ·  1\.0\.0\+[0-9a-f]{7}  ·  MIT licensed$", text);

                Screenshots.Capture(launcher.Window, "01_launcher");
            }
        }

        [AvaloniaFact]
        public void Dicom_to_nifti_button_opens_the_forward_window()
        {
            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                Assert.True(window.IsVisible);
                Assert.True(vm.ShowTreeEmptyHint);
                Assert.Equal(0, launcher.Services.FolderPicker.Calls);
                Screenshots.Capture(window, "02_dicom_to_nifti_empty");
            }
        }

        [AvaloniaFact]
        public void Nifti_to_dicom_button_opens_the_reverse_window()
        {
            using (var launcher = new LauncherSession())
            {
                var window = launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
                Assert.True(window.IsVisible);
                Assert.False(vm.IsServerMode);
                Assert.Equal("Run Server", vm.ServerButtonText);
                Screenshots.Capture(window, "03_nifti_to_dicom_empty");
            }
        }
    }
}
