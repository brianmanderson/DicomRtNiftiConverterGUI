namespace DicomRtNifti.Web.Shell;

/// <summary>
/// One page group of the web app. A feature is a folder <c>Features/&lt;Name&gt;/</c> holding its
/// pages and one class implementing this interface; the shell finds the class at startup
/// (<see cref="FeatureCatalog.Discover"/>), registers its services and endpoints, and builds the
/// menu from it. Adding a page is therefore adding a folder: no brief edits <c>Program.cs</c>
/// or the menu (plan sections 3.1 and 8.1).
///
/// Implementations need a public parameterless constructor and must not hold state: one
/// instance describes the feature for the life of the process.
/// </summary>
public interface IWebFeature
{
    /// <summary>The menu label and page heading.</summary>
    string Title { get; }

    /// <summary>Which menu group the feature sits in.</summary>
    NavSection Section { get; }

    /// <summary>Position in the menu, ascending; within a section, ties break by title.</summary>
    int Order { get; }

    /// <summary>
    /// The routable component the menu links to. Its single <c>@page</c> route is the feature's
    /// route; a route carries no parameter, since pages never put an identifier in a URL.
    /// </summary>
    Type Page { get; }

    /// <summary>One sentence: what a user does on this page.</summary>
    string Summary { get; }

    /// <summary>The command line that does the same thing, shown on the page.</summary>
    CliEquivalent Cli { get; }

    /// <summary>Registers the feature's services (job workers, stores). Called once, before the host is built.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    /// <summary>Maps endpoints other than pages (downloads, uploads). Called once, after the shell's own.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>The menu groups, in menu order.</summary>
public enum NavSection
{
    Start,
    Cohort,
    MoreInputs,
    Server,
}

/// <summary>
/// The CLI equivalent of a page (plan section 4). <see cref="Command"/> is null when the CLI has
/// none (an upload); <see cref="Planned"/> marks a mode a later brief adds to the CLI, which the
/// page must not present as available.
/// </summary>
public sealed record CliEquivalent(string? Command, string? Note = null, bool Planned = false);

public static class NavSectionExtensions
{
    public static string DisplayName(this NavSection section) => section switch
    {
        NavSection.Start => "Start",
        NavSection.Cohort => "Cohort",
        NavSection.MoreInputs => "Reverse and intake",
        NavSection.Server => "Server",
        _ => section.ToString(),
    };
}
