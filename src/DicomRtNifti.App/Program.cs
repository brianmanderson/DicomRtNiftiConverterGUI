using System;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;

// The headless GUI tests drive server-mode ticks directly instead of waiting on the 10 s timer.
[assembly: InternalsVisibleTo("DicomRtNifti.App.Tests")]

namespace DicomRtNifti.App
{
    /// <summary>
    /// Avalonia desktop entry point. The headless conversion CLI lives in the separate
    /// DicomRtNifti.Cli executable; this process is GUI-only.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Parity with the CLI's entry point. fo-dicom registers the code-page provider itself
            // (an ISO_IR 101 structure set decodes without this; see NonAsciiRoiNameTests), so this
            // is belt and braces for any other legacy-charset decode in the process.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Used by the Avalonia previewer/designer tooling as well as Main.
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
