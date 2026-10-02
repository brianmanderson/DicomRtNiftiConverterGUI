using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// Every top-level window currently open, maintained from the same routed events the desktop
    /// lifetime uses (Window.WindowOpenedEvent / WindowClosedEvent), since the headless session has
    /// no lifetime to ask. Registered once per process; the routed events are static.
    /// </summary>
    public static class OpenWindows
    {
        private static bool _installed;
        private static readonly List<Window> Windows = new List<Window>();

        public static IReadOnlyList<Window> Current
        {
            get { Install(); return Windows.ToList(); }
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
            {
                if (sender is Window w && !Windows.Contains(w)) Windows.Add(w);
            });
            Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) =>
            {
                if (sender is Window w) Windows.Remove(w);
            });
        }
    }

    /// <summary>
    /// The launcher as the App composes it (<see cref="TestServices"/>), shown, plus the ways a
    /// user opens further windows from it: a real click on one of its buttons, or a command on a
    /// window it opened (the Help buttons). Everything a click or command opens goes through the
    /// production code-behind and view-models, so what the tests drive is wired exactly as it is
    /// for a user. Disposing closes the launcher and every window opened through it.
    /// </summary>
    public sealed class LauncherSession : IDisposable
    {
        private readonly List<Window> _opened = new List<Window>();

        public LauncherSession()
        {
            OpenWindows.Install();
            Services = new TestServices();
            ViewModel = Services.NewLauncherViewModel();
            Window = new LauncherWindow { DataContext = ViewModel };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public TestServices Services { get; }
        public LauncherViewModel ViewModel { get; }
        public LauncherWindow Window { get; }

        /// <summary>Clicks the launcher button whose caption starts with <paramref name="caption"/>.</summary>
        public void Click(string caption)
        {
            Button button = Window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text != null && t.Text.StartsWith(caption, StringComparison.Ordinal)));
            Assert.True(button != null, $"no launcher button captioned '{caption}'");

            Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)
                ?? throw new InvalidOperationException("button is not in the launcher's visual tree");
            Window.MouseMove(centre);
            Window.MouseDown(centre, MouseButton.Left);
            Window.MouseUp(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// Clicks the launcher button and returns the one window that opened, which must be a
        /// <typeparamref name="TWindow"/> carrying a <typeparamref name="TViewModel"/>.
        /// </summary>
        public TWindow Open<TWindow, TViewModel>(string caption, out TViewModel viewModel)
            where TWindow : Window
        {
            var window = OpenedBy<TWindow>(() => Click(caption), $"clicking '{caption}'");
            viewModel = Assert.IsType<TViewModel>(window.DataContext);
            return window;
        }

        /// <summary>
        /// Runs <paramref name="open"/> (a view-model command, say) and returns the one
        /// <typeparamref name="TWindow"/> it opened; it is closed with the session.
        /// </summary>
        public TWindow OpenedBy<TWindow>(Action open, string what) where TWindow : Window
        {
            var before = OpenWindows.Current;
            open();
            Dispatcher.UIThread.RunJobs();
            var opened = OpenWindows.Current.Except(before).ToList();
            Assert.True(opened.Count == 1, $"expected one new window after {what}, found {opened.Count}");
            var window = Assert.IsType<TWindow>(opened[0]);
            _opened.Add(window);
            return window;
        }

        public void Dispose()
        {
            // Children first (a Help window owned by a workflow window), then the launcher.
            foreach (var w in Enumerable.Reverse(_opened))
                if (w.IsVisible) w.Close();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
