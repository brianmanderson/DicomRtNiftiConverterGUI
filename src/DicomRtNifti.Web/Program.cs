using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Shell;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web;

/// <summary>
/// The web host (preview): a Blazor Web App, Interactive Server, with Core in-process (D-30).
/// It listens on loopback only until authentication exists (D-33): <see cref="BindingGuard"/>
/// refuses any other address before the server opens it. Pages come from the features found
/// in this assembly (<see cref="FeatureCatalog"/>), so page briefs never edit this file.
/// </summary>
public sealed class Program
{
    /// <summary>Exit code for a configuration the server refuses to start with, as the CLI's usage errors.</summary>
    public const int RefusedExitCode = 2;

    public static int Main(string[] args)
    {
        try
        {
            BuildApp(args).Run();
            return 0;
        }
        catch (Exception ex) when (StartupRefusal(ex) is { } message)
        {
            Console.Error.WriteLine(message);
            return RefusedExitCode;
        }
    }

    public static WebApplication BuildApp(string[] args)
    {
        // The application name locates the static-asset manifests; pin it, so the app is the same
        // whether this assembly is the entry point or a test process hosts it.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        // Serve blazor.web.js and the CSS bundle from the build output in every environment, so a
        // Release `dotnet run` works as a Development one does; a published app has no runtime
        // manifest and this does nothing.
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

        builder.Services.AddOptions<WebOptions>()
            .Bind(builder.Configuration.GetSection(WebOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();
        builder.Services.AddSingleton<DataRoots>();
        builder.Services.AddSingleton<DataRootResolver>();
        builder.Services.AddSingleton<PathRedactor>();
        builder.Services.AddSingleton<HostStatus>();
        builder.Services.AddHostedService<BindingGuardService>();
        RedactingLoggerFactory.Install(builder.Services);

        var features = FeatureCatalog.Discover(typeof(Program).Assembly);
        builder.Services.AddSingleton(features);
        foreach (var feature in features.All)
            feature.Feature.ConfigureServices(builder.Services, builder.Configuration);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        app.UseSecurityHeaders();
        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapHealth();
        foreach (var feature in features.All)
            feature.Feature.MapEndpoints(app);
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    /// <summary>The message to print for a startup the server refuses, or null for any other failure.</summary>
    public static string? StartupRefusal(Exception? exception)
    {
        switch (exception)
        {
            case null:
                return null;
            case BindingRefusedException refused:
                return refused.Message;
            case OptionsValidationException invalid:
                return "Refusing to start: the configuration is invalid." + Environment.NewLine
                    + string.Join(Environment.NewLine, invalid.Failures.Select(f => "  " + f));
            case AggregateException aggregate:
                return aggregate.InnerExceptions.Select(StartupRefusal).FirstOrDefault(m => m is not null);
            default:
                return StartupRefusal(exception.InnerException);
        }
    }
}
