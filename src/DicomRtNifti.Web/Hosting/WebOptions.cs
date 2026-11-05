namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// Server settings, bound from the <c>Web</c> configuration section (appsettings.json or
/// environment variables such as <c>Web__DataRoots__0__Path</c>). Nothing here is editable from
/// the browser.
///
/// This is the per-server third of the settings split (D-35): roots, limits, lanes and the key
/// root are the server's; the salt and association sets belong to an output project, and export
/// presets and recent folders to a user, both stored server-side under <see cref="StateRoot"/> by
/// later briefs. The keys later briefs read are declared here now, so a page brief never has to
/// edit the hosting layer to get one.
/// </summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>The folders the browser may name, as <c>(id, root-relative path)</c> pairs.</summary>
    public List<DataRootOptions> DataRoots { get; set; } = new();

    /// <summary>Where output projects are written; browsable as the root <c>output</c>.</summary>
    public string? OutputRoot { get; set; }

    /// <summary>Where anonymization keys go, one folder per project; never browsable, never served.</summary>
    public string? KeyRoot { get; set; }

    /// <summary>Where uploaded cases are extracted, one folder per job (W4-N).</summary>
    public string? UploadRoot { get; set; }

    /// <summary>Jobs, scan snapshots, audit and per-user state (W4-E onwards); never browsable.</summary>
    public string? StateRoot { get; set; }

    /// <summary>
    /// Whether CLI-equivalent commands may print absolute server paths instead of root tokens
    /// (plan section 4), for a single-user loopback deployment. It never relaxes
    /// <see cref="PathRedactor"/>, which is applied to every response and log regardless.
    /// </summary>
    public bool ShowAbsolutePaths { get; set; }

    public WebLimits Limits { get; set; } = new();

    public WebLanes Lanes { get; set; } = new();
}

public sealed class DataRootOptions
{
    /// <summary>Lower-case letters, digits and hyphens, starting with a letter; shown to the browser.</summary>
    public string Id { get; set; } = "";

    /// <summary>An absolute server path. Never shown to the browser.</summary>
    public string Path { get; set; } = "";

    public RootAccess Access { get; set; } = RootAccess.Read;
}

public enum RootAccess
{
    Read,
    ReadWrite,
}

/// <summary>Size and retention limits (plan section 3.3; D-39, D-40).</summary>
public sealed class WebLimits
{
    public long UploadBytes { get; set; } = 512L * 1024 * 1024;

    public long DownloadZipBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int UploadRetentionHours { get; set; } = 24;
}

/// <summary>
/// The job lanes (plan section 3.2): one heavy worker by default because a single conversion
/// already saturates the machine.
/// </summary>
public sealed class WebLanes
{
    public int MaxHeavyWorkers { get; set; } = 1;

    public int MaxLightWorkers { get; set; } = 1;

    public int QueueCapacity { get; set; } = 16;
}
