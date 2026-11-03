namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// <c>GET /health</c>: the version and the SimpleITK native probe as JSON, 200 when the native
/// loaded and 503 when it did not, so a monitor or a CI step can fail on the status code alone.
/// It names no root and no path.
/// </summary>
public static class HealthEndpoint
{
    public const string Route = "/health";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (HostStatus status) => Results.Json(
            new
            {
                status = status.NativeOk ? "ok" : "degraded",
                version = status.Version,
                simpleitk_native = new { ok = status.NativeOk, probe = status.NativeProbe },
            },
            statusCode: status.NativeOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
        return endpoints;
    }
}
