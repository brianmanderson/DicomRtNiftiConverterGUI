using System;
using System.IO;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json;

namespace DicomRtNifti.App.Services
{
    /// <summary>
    /// Window state the GUI restores on the next launch: the folders last used in each window and
    /// the reverse window's three Convert toggles. None of it is a preference the CLI or Core
    /// reads, so it lives beside <c>settings.json</c> in the GUI's own <c>gui_state.json</c>
    /// rather than in <see cref="DicomRtNifti.Core.Models.AppSettings"/>. The forward window's
    /// export toggles already have settings fields and stay there.
    /// </summary>
    public class GuiState
    {
        public string LastInputFolder { get; set; } = "";
        public string LastOutputFolder { get; set; } = "";
        public string LastReverseFolder { get; set; } = "";
        public bool ReverseConvertImage { get; set; } = true;
        public bool ReverseConvertStructures { get; set; } = true;
        public bool ReverseConvertDoses { get; set; } = true;
    }

    /// <summary>
    /// Loads and saves <see cref="GuiState"/> with the same integrity rules as
    /// <see cref="SettingsService"/>: writes are atomic, and a file that exists but does not parse
    /// is never written over. In that case the GUI runs on defaults in memory and
    /// <see cref="BlockedReason"/> says why nothing is being saved.
    ///
    /// Every <see cref="Update"/> re-reads the file before applying its change, so two windows
    /// open at once each persist their own fields without reverting the other's.
    /// </summary>
    public class GuiStateStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        public GuiStateStore() : this(DefaultPath) { }

        public GuiStateStore(string path)
        {
            FilePath = path;
        }

        /// <summary>%AppData%\DicomToNifti\gui_state.json (XDG config on Linux/macOS), beside settings.json.</summary>
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DicomToNifti", "gui_state.json");

        public string FilePath { get; }

        /// <summary>Set once the file proved unreadable; nothing is written after that.</summary>
        public string BlockedReason { get; private set; }

        public bool IsBlocked => BlockedReason != null;

        /// <summary>The stored state, defaults when the file is absent, defaults (and blocked) when it is unreadable.</summary>
        public GuiState Load()
        {
            if (IsBlocked || !File.Exists(FilePath))
                return new GuiState();
            try
            {
                return SettingsService.ReadJson<GuiState>(FilePath, "GUI state");
            }
            catch (InvalidDataException ex)
            {
                BlockedReason = ex.Message;
                return new GuiState();
            }
        }

        /// <summary>
        /// Applies <paramref name="change"/> to the stored state and writes it back. Returns false,
        /// writing nothing, when the store is blocked or the write fails; <see cref="BlockedReason"/>
        /// or the returned <paramref name="error"/> says why.
        /// </summary>
        public bool Update(Action<GuiState> change, out string error)
        {
            error = null;
            var state = Load();
            if (IsBlocked)
            {
                error = BlockedReason;
                return false;
            }

            change(state);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                AtomicFileWriter.WriteAllText(FilePath, JsonConvert.SerializeObject(state, JsonSettings));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"Could not save GUI state to '{FilePath}': {ex.Message}";
                return false;
            }
        }
    }
}
