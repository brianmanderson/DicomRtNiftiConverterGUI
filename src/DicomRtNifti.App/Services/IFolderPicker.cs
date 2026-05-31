using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Dicom_RT_images_Csharp.Services
{
    /// <summary>
    /// Folder-selection abstraction so view-models can prompt for a folder without
    /// referencing a Window (the WPF VMs used System.Windows.Forms.FolderBrowserDialog
    /// directly). Lives in the same namespace as the Core services so existing
    /// `using Dicom_RT_images_Csharp.Services;` directives pick it up.
    /// </summary>
    public interface IFolderPicker
    {
        /// <summary>Returns the selected folder's local path, or null if cancelled/unavailable.</summary>
        Task<string> PickFolderAsync(string title, string suggestedPath);
    }

    /// <summary>Avalonia implementation backed by the active window's IStorageProvider.</summary>
    public class AvaloniaFolderPicker : IFolderPicker
    {
        public async Task<string> PickFolderAsync(string title, string suggestedPath)
        {
            var top = ResolveTopLevel();
            if (top?.StorageProvider == null || !top.StorageProvider.CanPickFolder)
                return null;

            IStorageFolder start = null;
            if (!string.IsNullOrEmpty(suggestedPath) && Directory.Exists(suggestedPath))
                start = await top.StorageProvider.TryGetFolderFromPathAsync(suggestedPath);

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        private static TopLevel ResolveTopLevel()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            return null;
        }
    }
}
