using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Resolves the currently-active desktop window, used as the owner for modal dialogs
    /// opened from view-models (Avalonia's ShowDialog requires an owner; the WPF VMs used
    /// Application.Current.MainWindow).
    /// </summary>
    public static class AppWindows
    {
        public static Window Active
        {
            get
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
                return null;
            }
        }
    }
}
