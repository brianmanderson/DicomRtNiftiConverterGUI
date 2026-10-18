using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DicomRtNifti.App.Services;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// Step 4 of W2-D: toggles and last-used folders persist the moment they change (not only on
    /// Convert) and come back in the next window; an unreadable state file is never written over;
    /// and the app follows the OS theme instead of forcing Light.
    /// </summary>
    public class PersistenceAndThemeTests
    {
        [AvaloniaFact]
        public void Forward_toggles_and_folders_persist_on_change_and_return_in_the_next_window()
        {
            using (new UserSettingsGuard())
            using (var testCase = new TestCase())
            using (var launcher = new LauncherSession())
            {
                var first = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                vm.InputFolder = testCase.InputFolder;
                vm.OutputFolder = testCase.OutputFolder;
                vm.ExportImages = false;
                vm.IncludeStructures = false;
                vm.IncludeDose = false;
                vm.OnlyExportSpecificRois = true;
                vm.AnonymizeExport = true;
                vm.SpecifyOutputSpacing = true;
                vm.ExportDicomMetadata = true;

                // On disk already: nothing was converted.
                var saved = new SettingsService().LoadSettings();
                Assert.False(saved.ExportImages);
                Assert.False(saved.IncludeStructures);
                Assert.False(saved.IncludeDose);
                Assert.True(saved.OnlyExportSpecificRois);
                Assert.True(saved.AnonymizeExport);
                Assert.True(saved.SpecifyOutputSpacing);
                Assert.True(saved.ExportDicomMetadata);
                var gui = new GuiStateStore().Load();
                Assert.Equal(testCase.InputFolder, gui.LastInputFolder);
                Assert.Equal(testCase.OutputFolder, gui.LastOutputFolder);

                first.Close();
                Dispatcher.UIThread.RunJobs();

                launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var next);
                Assert.Equal(testCase.InputFolder, next.InputFolder);
                Assert.Equal(testCase.OutputFolder, next.OutputFolder);
                Assert.False(next.ExportImages);
                Assert.False(next.IncludeStructures);
                Assert.False(next.IncludeDose);
                Assert.True(next.OnlyExportSpecificRois);
                Assert.True(next.AnonymizeExport);
                Assert.True(next.SpecifyOutputSpacing);
                Assert.True(next.ExportDicomMetadata);
            }
        }

        [AvaloniaFact]
        public void Reverse_toggles_and_folder_persist_on_change_and_return_in_the_next_window()
        {
            using (new UserSettingsGuard())
            using (var testCase = new TestCase())
            using (var launcher = new LauncherSession())
            {
                var first = launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
                Assert.True(vm.ConvertImage && vm.ConvertStructures && vm.ConvertDoses);
                vm.RootFolder = testCase.Root;
                vm.ConvertImage = false;
                vm.ConvertDoses = false;

                var gui = new GuiStateStore().Load();
                Assert.Equal(testCase.Root, gui.LastReverseFolder);
                Assert.False(gui.ReverseConvertImage);
                Assert.True(gui.ReverseConvertStructures);
                Assert.False(gui.ReverseConvertDoses);

                first.Close();
                Dispatcher.UIThread.RunJobs();

                launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var next);
                Assert.Equal(testCase.Root, next.RootFolder);
                Assert.False(next.ConvertImage);
                Assert.True(next.ConvertStructures);
                Assert.False(next.ConvertDoses);
            }
        }

        [Fact]
        public void Gui_state_updates_from_two_windows_do_not_revert_each_other()
        {
            string path = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N"), "gui_state.json");
            try
            {
                var forward = new GuiStateStore(path);
                var reverse = new GuiStateStore(path);
                Assert.True(forward.Update(s => s.LastInputFolder = "in", out _));
                Assert.True(reverse.Update(s => s.ReverseConvertDoses = false, out _));
                Assert.True(forward.Update(s => s.LastOutputFolder = "out", out _));

                var state = new GuiStateStore(path).Load();
                Assert.Equal("in", state.LastInputFolder);
                Assert.Equal("out", state.LastOutputFolder);
                Assert.False(state.ReverseConvertDoses);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path), recursive: true);
            }
        }

        [Fact]
        public void An_unreadable_gui_state_file_is_never_written_over()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DicomRtNifti.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "gui_state.json");
            File.WriteAllText(path, "{ \"LastInputFolder\": \"trunc");
            try
            {
                var store = new GuiStateStore(path);
                var state = store.Load();
                Assert.True(store.IsBlocked);
                Assert.Equal("", state.LastInputFolder);

                string error;
                Assert.False(store.Update(s => s.LastInputFolder = "elsewhere", out error));
                Assert.Contains(path, error);
                Assert.Equal("{ \"LastInputFolder\": \"trunc", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [AvaloniaFact]
        public void The_app_follows_the_os_theme_and_its_surfaces_follow_the_variant()
        {
            var app = Application.Current;
            Assert.NotNull(app);
            // No forced variant: unset (null) or Default both defer to the platform's (the OS)
            // light/dark setting. App.axaml used to pin Light here.
            var requested = app.RequestedThemeVariant;
            Assert.True(requested == null || requested == ThemeVariant.Default, $"App.axaml forces {requested}");

            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                var window = launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out _);
                try
                {
                    app.RequestedThemeVariant = ThemeVariant.Light;
                    Dispatcher.UIThread.RunJobs();
                    var light = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

                    app.RequestedThemeVariant = ThemeVariant.Dark;
                    Dispatcher.UIThread.RunJobs();
                    var dark = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

                    Assert.NotEqual(light, dark);
                    Assert.True(light.R > 0xC0 && dark.R < 0x40, $"light {light}, dark {dark}");
                    Screenshots.Capture(window, "12_dicom_to_nifti_dark");
                }
                finally
                {
                    app.RequestedThemeVariant = requested;
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
    }
}
