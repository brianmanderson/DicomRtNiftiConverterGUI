using System.Text;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Web.Hosting;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// How a job shows a server path in what it records and emits: the root token of the configured
/// folder that holds it, <c>archive:2024/H&amp;N</c>, with '/' separators (the form
/// <see cref="ResolvedPath.Presented"/> uses), and <see cref="PathRedactor.OutsideRoots"/> for a
/// path under none. This is the <see cref="PathPresenter"/> handed to Core's command-line renderer
/// and document builders, so a job's CLI equivalent and its result document name the same folders
/// the browser does. With <c>Web:ShowAbsolutePaths</c> (one user, loopback) the command line
/// shows the server path instead; the documents never do.
/// </summary>
public sealed class RootPathPresenter
{
    private readonly (string Path, string Id)[] _folders;
    private readonly bool _absoluteInCommands;

    public RootPathPresenter(DataRoots roots, IOptions<WebOptions> options)
    {
        // Longest first, so a folder nested in another presents as its own id.
        _folders = roots.AllFolders.Select(f => (f.Path, f.Id)).OrderByDescending(f => f.Path.Length).ToArray();
        _absoluteInCommands = options.Value.ShowAbsolutePaths;
    }

    /// <summary>For result documents: always root tokens.</summary>
    public PathPresenter Documents => Present;

    /// <summary>For CLI-equivalent commands: root tokens unless the server shows absolute paths.</summary>
    public PathPresenter Commands => _absoluteInCommands ? CohortDocuments.Identity : Present;

    public string Present(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return fullPath;
        foreach (var (path, id) in _folders)
        {
            if (!DataRootResolver.IsWithin(fullPath, path))
                continue;
            string rest = fullPath.Length > path.Length ? fullPath[path.Length..] : "";
            return id + ":" + rest.Replace('\\', '/').Trim('/');
        }

        return PathRedactor.OutsideRoots;
    }
}

/// <summary>Joins a command line for display, quoting what a shell would split.</summary>
public static class CommandLineText
{
    public const string Program = "DicomRtNifti.Cli";

    public static string Join(IEnumerable<string> args)
    {
        var text = new StringBuilder(Program);
        foreach (string arg in args)
        {
            text.Append(' ');
            bool quote = arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or ';' or '<' or '>');
            text.Append(quote ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg);
        }

        return text.ToString();
    }
}
