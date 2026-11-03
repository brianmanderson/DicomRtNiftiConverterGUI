using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// Thrown at startup when the server would listen where it must not. The message lists each
/// refused address and why; it carries URLs only, so it is safe to print.
/// </summary>
public sealed class BindingRefusedException : Exception
{
    public BindingRefusedException(IReadOnlyList<string> refusals)
        : base("Refusing to start: " + string.Join(" ", refusals)
            + " The web app listens on loopback only (127.0.0.1, ::1, localhost) until authentication is configured.")
    {
        Refusals = refusals;
    }

    public IReadOnlyList<string> Refusals { get; }
}

/// <summary>
/// Fails closed on where the server listens (D-33, plan section 6): any address that is not
/// loopback is refused while no authentication is enforced, and a non-loopback address is also
/// refused over plain HTTP (W4-P adds TLS and the trusted-proxy exception).
///
/// "Authentication is enforced" is read from what is registered, not from a configuration
/// claim: at least one authentication scheme, and a fallback authorization policy that denies
/// anonymous users. This scaffold registers neither, so every non-loopback address is refused.
/// </summary>
public static class BindingGuard
{
    /// <summary>Where Kestrel listens when nothing is configured.</summary>
    public const string KestrelDefaultUrl = "http://localhost:5000";

    /// <summary>
    /// Every address the configuration asks Kestrel to listen on: <c>urls</c> (from
    /// <c>--urls</c>, <c>ASPNETCORE_URLS</c>, <c>DOTNET_URLS</c> or appsettings <c>Urls</c>),
    /// <c>http_ports</c> and <c>https_ports</c> (which listen on every interface), and each
    /// <c>Kestrel:Endpoints:*:Url</c>. Where Kestrel would let one source override another, all
    /// are still checked.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredUrls(IConfiguration configuration)
    {
        var urls = new List<string>();
        urls.AddRange(SplitList(configuration["urls"]));
        foreach (string port in SplitList(configuration["http_ports"]))
            urls.Add($"http://*:{port}");
        foreach (string port in SplitList(configuration["https_ports"]))
            urls.Add($"https://*:{port}");
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            string? url = endpoint["Url"];
            urls.Add(string.IsNullOrWhiteSpace(url) ? $"(Kestrel:Endpoints:{endpoint.Key} has no Url)" : url.Trim());
        }

        if (urls.Count == 0)
            urls.Add(KestrelDefaultUrl);
        return urls;
    }

    /// <summary>
    /// Whether Kestrel would listen on loopback only for this address: <c>localhost</c>, or an IP
    /// literal in 127.0.0.0/8 or ::1. Wildcards, other IPs, host names (Kestrel binds those on
    /// every interface), Unix sockets, named pipes and anything unparsable are not.
    /// </summary>
    public static bool IsLoopback(string url)
    {
        BindingAddress address;
        try
        {
            address = BindingAddress.Parse(url);
        }
        catch (FormatException)
        {
            return false;
        }

        if (address.IsUnixPipe || address.IsNamedPipe)
            return false;
        if (!address.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !address.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;
        if (address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        string host = address.Host.StartsWith('[') && address.Host.EndsWith(']') ? address.Host[1..^1] : address.Host;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>One reason per address that must not be listened on; empty when all are acceptable.</summary>
    public static IReadOnlyList<string> Refusals(IEnumerable<string> urls, bool authenticationEnforced)
    {
        var refusals = new List<string>();
        foreach (string url in urls)
        {
            if (IsLoopback(url))
                continue;
            if (!authenticationEnforced)
                refusals.Add($"{url} is not a loopback address and no authentication is configured.");
            else if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                refusals.Add($"{url} is not a loopback address and is not HTTPS.");
        }

        return refusals;
    }

    /// <summary>Whether a scheme is registered and the fallback policy denies anonymous users.</summary>
    public static async Task<bool> IsAuthenticationEnforcedAsync(IServiceProvider services)
    {
        var schemes = services.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null || !(await schemes.GetAllSchemesAsync()).Any())
            return false;

        var policies = services.GetService<IAuthorizationPolicyProvider>();
        var fallback = policies is null ? null : await policies.GetFallbackPolicyAsync();
        return fallback is not null && fallback.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any();
    }

    private static IEnumerable<string> SplitList(string? value) =>
        (value ?? "").Split(';', ',').Select(s => s.Trim()).Where(s => s.Length > 0);
}

/// <summary>
/// Runs <see cref="BindingGuard"/> when the host starts. <see cref="StartingAsync"/> runs before
/// any hosted service starts, the web server included, so a refused address is never opened.
/// <see cref="StartedAsync"/> checks again what the server actually bound, as a backstop against
/// an address configured by a route the configuration check does not read; that check stops the
/// host after the bind, so it is the second line, not the first.
/// </summary>
public sealed class BindingGuardService : IHostedLifecycleService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly IServer _server;
    private readonly ILogger<BindingGuardService> _logger;

    public BindingGuardService(
        IConfiguration configuration, IServiceProvider services, IServer server, ILogger<BindingGuardService> logger)
    {
        _configuration = configuration;
        _services = services;
        _server = server;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        bool enforced = await BindingGuard.IsAuthenticationEnforcedAsync(_services);
        var refusals = BindingGuard.Refusals(BindingGuard.ConfiguredUrls(_configuration), enforced);
        if (refusals.Count > 0)
            throw new BindingRefusedException(refusals);
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            return; // an in-memory test server has no addresses
        bool enforced = await BindingGuard.IsAuthenticationEnforcedAsync(_services);
        var refusals = BindingGuard.Refusals(addresses, enforced);
        if (refusals.Count == 0)
            return;
        _logger.LogCritical("The server bound an address the binding guard refuses; stopping.");
        throw new BindingRefusedException(refusals);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
