using System.Text;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.State;

/// <summary>
/// A user's export presets (plan section 3.5): named sets of the export toggles that prefill
/// Export setup. A preset is the desktop app's own settings file, <see cref="AppSettings"/>, kept
/// per user and per preset under <c>StateRoot/users/&lt;user&gt;/presets/&lt;preset&gt;/settings.json</c>
/// and read and written by Core's <see cref="SettingsService"/>, so this type calls no filesystem
/// API. Server-side, never in the browser's storage: the fields carry no identifier, but the plan
/// keeps per-user state on the server (section 6).
///
/// What a preset carries is what <c>AppSettings</c> carries for an export: what to write, the ROI
/// rule, the output spacing, the metadata keywords and whether to anonymize. Never a salt or a key
/// location: those are the project's and the server's (D-36), so they are blanked on save and
/// ignored on load.
/// </summary>
public sealed class UserPreferences
{
    public const string UsersFolder = "users";
    public const string PresetsFolder = "presets";
    public const string SettingsFileName = "settings.json";

    private readonly DataRoots _roots;
    private readonly DataRootResolver _resolver;

    public UserPreferences(DataRoots roots, DataRootResolver resolver)
    {
        _roots = roots;
        _resolver = resolver;
    }

    /// <summary>True when the server has a state root to keep presets in.</summary>
    public bool IsConfigured => _roots.Folder(ServerFolder.State) is not null;

    /// <summary>The user's presets, by name.</summary>
    public IReadOnlyList<string> Presets(string user)
    {
        if (!IsConfigured)
            return Array.Empty<string>();
        var folder = _resolver.ResolveServerPath(ServerFolder.State, UserFolder(user) + "/" + PresetsFolder);
        return DataRootResolver.DirectoryExists(folder) ? _resolver.ListSubfolders(folder) : Array.Empty<string>();
    }

    /// <summary>Reads a preset; one never saved reads as the defaults.</summary>
    /// <exception cref="InvalidDataException">The preset's file exists but does not parse (Core's refusal).</exception>
    public AppSettings Load(string user, string preset) => Settings(user, preset).LoadSettings();

    /// <summary>Saves the export toggles of <paramref name="options"/> as a preset, atomically.</summary>
    public void Save(string user, string preset, CohortExportOptions options) =>
        Settings(user, preset).SaveSettings(ToPreset(options));

    /// <summary>
    /// Prefills <paramref name="options"/> from a preset: what to write, the ROI rule, the spacing,
    /// the metadata keywords and anonymization. Selection, folders and the salt are left alone.
    /// </summary>
    public static void Apply(AppSettings preset, CohortExportOptions options)
    {
        options.ExportImages = preset.ExportImages;
        options.ExportStructures = preset.IncludeStructures;
        options.ExportDoses = preset.IncludeDose;
        options.OnlyAssociatedRois = preset.OnlyExportSpecificRois;
        options.Anonymize = preset.AnonymizeExport;
        options.OutputSpacing = preset.SpecifyOutputSpacing
            ? new[] { preset.OutputSpacingX, preset.OutputSpacingY, preset.OutputSpacingZ }
            : null;
        bool metadata = preset.ExportDicomMetadata;
        options.ImageKeywords = metadata ? new List<string>(preset.MetadataImageTagKeywords ?? new()) : new List<string>();
        options.StructureKeywords = metadata ? new List<string>(preset.MetadataStructureTagKeywords ?? new()) : new List<string>();
        options.DoseKeywords = metadata ? new List<string>(preset.MetadataDoseTagKeywords ?? new()) : new List<string>();
    }

    /// <summary>The preset that <see cref="Apply"/> turns back into the same export toggles.</summary>
    public static AppSettings ToPreset(CohortExportOptions options)
    {
        var spacing = options.OutputSpacing;
        return new AppSettings
        {
            ExportImages = options.ExportImages,
            IncludeStructures = options.ExportStructures,
            IncludeDose = options.ExportDoses,
            OnlyExportSpecificRois = options.OnlyAssociatedRois,
            AnonymizeExport = options.Anonymize,
            // Never the salt or a key location (D-36): a preset is the user's, the salt the project's.
            HashSalt = "",
            AnonymizationKeyPath = "",
            SpecifyOutputSpacing = spacing is not null,
            OutputSpacingX = spacing?[0] ?? 1.0,
            OutputSpacingY = spacing?[1] ?? 1.0,
            OutputSpacingZ = spacing?[2] ?? 1.0,
            ExportDicomMetadata = options.HasMetadataSelections,
            MetadataImageTagKeywords = new List<string>(options.ImageKeywords ?? new()),
            MetadataStructureTagKeywords = new List<string>(options.StructureKeywords ?? new()),
            MetadataDoseTagKeywords = new List<string>(options.DoseKeywords ?? new()),
        };
    }

    /// <summary>The refusal for a preset name, or null; the same rule as an association set's.</summary>
    public static string? CheckPresetName(string? name) =>
        ProjectStore.CheckSetName(name)?.Replace("An association set", "A preset");

    /// <summary>
    /// A user id as one folder name: letters, digits, '.', '-' kept, every other character as
    /// <c>_</c> and its UTF-8 bytes in hex (<c>local:me</c> is <c>local_3ame</c>), so two ids never share a folder.
    /// </summary>
    public static string UserFolder(string user)
    {
        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("A user id is required.", nameof(user));
        var name = new StringBuilder(UsersFolder + "/");
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in user.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '.' or '-'))
            {
                name.Append((char)rune.Value);
                continue;
            }

            name.Append('_');
            int length = rune.EncodeToUtf8(utf8);
            foreach (byte b in utf8[..length])
                name.Append(b.ToString("x2"));
        }

        return name.ToString();
    }

    private SettingsService Settings(string user, string preset)
    {
        if (CheckPresetName(preset) is { } refused)
            throw new ArgumentException(refused, nameof(preset));
        var folder = _resolver.ResolveServerPath(ServerFolder.State, $"{UserFolder(user)}/{PresetsFolder}/{preset}");
        return new SettingsService(folder.FullPath);
    }
}
