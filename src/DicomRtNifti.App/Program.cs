using System;
using Avalonia;

namespace Dicom_RT_images_Csharp
{
    /// <summary>
    /// Avalonia desktop entry point. The headless conversion CLI lives in the separate
    /// DicomRtNifti.Cli executable; this process is GUI-only.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Used by the Avalonia previewer/designer tooling as well as Main.
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
