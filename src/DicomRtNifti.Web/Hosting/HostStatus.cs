using System.Reflection;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// What the Home page and <c>/health</c> report about this process: the version (the same
/// <c>1.0.0+&lt;sha&gt;</c> the CLI's --version prints) and Core's SimpleITK native probe. The
/// native either loaded in this process or it did not, so the probe runs once, on first use.
/// Its failure text names the folders the loader searched, so it is redacted before it is kept.
/// </summary>
public sealed class HostStatus
{
    private readonly Lazy<(bool Ok, string Probe)> _native;

    public HostStatus(PathRedactor redactor)
    {
        var assembly = typeof(HostStatus).Assembly;
        Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        _native = new Lazy<(bool, string)>(() =>
        {
            bool ok = SimpleItkNative.TryProbe(out string status);
            return (ok, redactor.Redact(status));
        });
    }

    /// <summary>The informational version, <c>1.0.0+&lt;full sha&gt;</c>.</summary>
    public string Version { get; }

    /// <summary>The version with its sha cut to seven characters, as the desktop launcher shows it.</summary>
    public string DisplayVersion
    {
        get
        {
            int plus = Version.IndexOf('+');
            return plus < 0 || Version.Length - plus - 1 <= 7 ? Version : Version[..(plus + 1 + 7)];
        }
    }

    public bool NativeOk => _native.Value.Ok;

    /// <summary>"SimpleITK native: OK (...)" or "SimpleITK native: FAILED TO LOAD -- ...", redacted.</summary>
    public string NativeProbe => _native.Value.Probe;
}
