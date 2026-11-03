using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        /// <summary>
        /// Render passes a capture may take before it gives up on the window settling. Three or four
        /// is normal (see <see cref="CaptureFrame"/>); reaching the bound means something keeps
        /// changing on screen, and a screenshot of it would show an arbitrary moment.
        /// </summary>
        private const int MaxRenderPasses = 20;

        /// <summary>Consecutive passes that must read the same pixels before a frame counts as settled.</summary>
        private const int SettledPasses = 3;

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

        /// <summary>Saves <paramref name="window"/> as &lt;name&gt;.png; see <see cref="CaptureFrame"/>.</summary>
        public static string Capture(Window window, string name) => CaptureFrame(window, name).Path;

        /// <summary>
        /// Captures <paramref name="window"/>, which must already be shown, once the rendered frame
        /// shows the window's current state, saves it as &lt;name&gt;.png, and asserts that the frame
        /// has the window's size and is not an implausibly small (blank) PNG. The returned frame
        /// keeps the pixels so a test can assert what the saved image shows.
        ///
        /// One render tick is not enough. The UI thread keeps at most one composition batch in
        /// flight (Avalonia's MediaContext): while a batch committed earlier, say mid-conversion,
        /// still waits for a tick, newer changes are held back, so the next tick draws that older
        /// batch and the newer state reaches the screen only on the tick after. Hence the loop: drain
        /// the dispatcher, force a tick, drain again (the finished batch releases the held-back
        /// changes as the next commit), read the frame, and stop once three consecutive passes read
        /// the same pixels. Two would not do: when the batch in flight changes nothing visible, its
        /// tick repeats the previous frame while the held-back changes are still one tick away.
        /// </summary>
        public static CapturedFrame CaptureFrame(Window window, string name)
        {
            CapturedFrame previous = null;
            int unchanged = 0;
            for (int pass = 1; pass <= MaxRenderPasses; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                using (WriteableBitmap bitmap = window.GetLastRenderedFrame())
                {
                    Assert.NotNull(bitmap);
                    var frame = new CapturedFrame(window, bitmap);
                    unchanged = previous != null && frame.HasSamePixels(previous) ? unchanged + 1 : 1;
                    previous = frame;
                    if (unchanged < SettledPasses)
                        continue;

                    Assert.True(bitmap.PixelSize.Width >= (int)window.Bounds.Width - 1 && bitmap.PixelSize.Height >= (int)window.Bounds.Height - 1,
                        $"frame {bitmap.PixelSize} smaller than the window {window.Bounds.Size}");

                    string path = Path.Combine(Directory, name + ".png");
                    using (var stream = File.Create(path))
                        bitmap.Save(stream);

                    Assert.True(new FileInfo(path).Length > 4096, $"{path} is implausibly small for a rendered window");
                    frame.Path = path;
                    return frame;
                }
            }

            Assert.Fail($"{name}: the rendered frame still changed after {MaxRenderPasses} render passes; refusing to save a frame that may predate the asserted state");
            return null;
        }
    }

    /// <summary>
    /// The pixels of one rendered frame, copied out of the headless framebuffer so a test can read
    /// back what the saved PNG shows.
    /// </summary>
    public sealed class CapturedFrame
    {
        private readonly Window _window;
        private readonly PixelFormat _format;
        private readonly byte[] _pixels;

        internal CapturedFrame(Window window, WriteableBitmap bitmap)
        {
            _window = window;
            using (ILockedFramebuffer fb = bitmap.Lock())
            {
                Assert.True(fb.Format.BitsPerPixel == 32, $"unexpected framebuffer format {fb.Format}");
                _format = fb.Format;
                Size = fb.Size;
                int rowLength = Size.Width * 4;
                _pixels = new byte[rowLength * Size.Height];
                for (int y = 0; y < Size.Height; y++)
                    Marshal.Copy(fb.Address + y * fb.RowBytes, _pixels, y * rowLength, rowLength);
            }
        }

        /// <summary>The saved PNG.</summary>
        public string Path { get; internal set; }

        public PixelSize Size { get; }

        internal bool HasSamePixels(CapturedFrame other) =>
            other.Size == Size && other._format.Equals(_format) && other._pixels.AsSpan().SequenceEqual(_pixels);

        /// <summary>
        /// The colour drawn at <paramref name="point"/>, given in <paramref name="visual"/>'s own
        /// coordinates (it may lie outside the visual, in its parent's margin, say).
        /// </summary>
        public Color PixelAt(Visual visual, Point point)
        {
            Point inWindow = visual.TranslatePoint(point, _window)
                ?? throw new InvalidOperationException($"{visual} is not in the captured window's visual tree");
            int x = (int)Math.Floor(inWindow.X * _window.RenderScaling);
            int y = (int)Math.Floor(inWindow.Y * _window.RenderScaling);
            Assert.True(x >= 0 && y >= 0 && x < Size.Width && y < Size.Height, $"({x}, {y}) lies outside the {Size} frame");

            int i = (y * Size.Width + x) * 4;
            if (_format.Equals(PixelFormat.Bgra8888))
                return Color.FromArgb(_pixels[i + 3], _pixels[i + 2], _pixels[i + 1], _pixels[i]);
            Assert.True(_format.Equals(PixelFormat.Rgba8888), $"unexpected framebuffer format {_format}");
            return Color.FromArgb(_pixels[i + 3], _pixels[i], _pixels[i + 1], _pixels[i + 2]);
        }
    }
}
