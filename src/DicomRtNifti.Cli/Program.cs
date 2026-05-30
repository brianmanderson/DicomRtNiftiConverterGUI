using System.Text;

namespace Dicom_RT_images_Csharp.Cli
{
    /// <summary>
    /// Cross-platform console entry point for the DICOM RT Toolkit headless CLI.
    ///
    /// Replaces the legacy WPF <c>App.OnStartup</c> <c>--headless</c> dispatch (which relied on
    /// the Win32 <c>AttachConsole</c> hack to reach a parent console) with a real console
    /// application that runs unchanged on Windows, Linux, and macOS.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // fo-dicom needs the legacy code-page encodings for some DICOM
            // SpecificCharacterSet values; unlike .NET Framework, .NET 8 does not
            // register them by default.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return HeadlessRunner.Run(args);
        }
    }
}
