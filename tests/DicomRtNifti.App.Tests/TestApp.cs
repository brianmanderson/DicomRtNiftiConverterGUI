using Avalonia;
using Avalonia.Headless;
using DicomRtNifti.App.Tests;
using Xunit;

// One headless Avalonia session for the assembly (a fresh, isolated Application per test); every
// [AvaloniaFact] runs on its dispatcher thread, so the collections must not run in parallel.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// Boots the real <see cref="DicomRtNifti.App.App"/> (App.axaml resources, Fluent theme, Inter
    /// font) on the headless windowing platform with Skia, so <c>CaptureRenderedFrame()</c> yields
    /// real pixels for the screenshots.
    ///
    /// The headless session runs the App without a desktop lifetime, and
    /// App.OnFrameworkInitializationCompleted only builds its composition root (Core services and
    /// the launcher) under one. <see cref="Support.LauncherSession"/> therefore builds the same
    /// root, with the OS folder dialog replaced by a scripted fake, and tracks the windows the
    /// launcher opens through the same routed events the desktop lifetime listens to.
    /// </summary>
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<DicomRtNifti.App.App>()
                .UseSkia()
                .WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
