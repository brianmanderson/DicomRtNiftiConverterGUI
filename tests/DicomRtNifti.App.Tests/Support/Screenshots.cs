using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// Renders a shown window to PNG. Files go to $DICOMRTNIFTI_SCREENSHOT_DIR when set (the
    /// conformance workflow uploads that folder as an artifact), otherwise to
    /// &lt;test output&gt;/screenshots. The PNGs are artifacts for a human to look at, not golden
    /// images: anti-aliasing differs across the three OS lanes even with the Inter font embedded.
    /// </summary>
    public static class Screenshots
    {
        public static string Directory
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("DICOMRTNIFTI_SCREENSHOT_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Captures <paramref name="window"/>, which must already be shown, and asserts that the
        /// frame has the window's size and is not an implausibly small (blank) PNG.
        /// </summary>
        public static string Capture(Window window, string name)
        {
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width >= (int)window.Bounds.Width - 1 && frame.PixelSize.Height >= (int)window.Bounds.Height - 1,
                $"frame {frame.PixelSize} smaller than the window {window.Bounds.Size}");

            string path = Path.Combine(Directory, name + ".png");
            using (var stream = File.Create(path))
                frame.Save(stream);

            Assert.True(new FileInfo(path).Length > 4096, $"{path} is implausibly small for a rendered window");
            return path;
        }
    }
}
