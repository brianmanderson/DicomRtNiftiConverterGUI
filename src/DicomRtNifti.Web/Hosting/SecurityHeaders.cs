namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// The headers every response carries (plan section 6): nothing is cached, because pages,
/// documents and downloads may carry PHI; no framing, sniffing or referrer; and a content
/// security policy that allows this origin only. Interactive Server rendering needs no inline
/// script or style and no eval, so the policy has no 'unsafe-*' source; its WebSocket is same
/// origin, which 'self' covers. HSTS belongs with TLS (W4-P).
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
        + "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        ["Cache-Control"] = "no-store",
        ["Pragma"] = "no-cache",
        ["Content-Security-Policy"] = ContentSecurityPolicy,
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "no-referrer",
        ["Cross-Origin-Opener-Policy"] = "same-origin",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
    };

    /// <summary>
    /// Sets <see cref="Values"/> on every response just before its headers are sent, so they
    /// override what an endpoint chose (static assets set their own cache headers) and survive
    /// the exception handler and status-code pages, which clear the response and re-execute.
    /// Register it first.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                foreach (var (name, value) in Values)
                    headers[name] = value;
                headers.Remove("Expires");
                return Task.CompletedTask;
            });
            return next(context);
        });
}
