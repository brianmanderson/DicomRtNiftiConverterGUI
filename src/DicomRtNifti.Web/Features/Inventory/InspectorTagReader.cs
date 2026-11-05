using System.Collections;
using System.Globalization;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;

namespace DicomRtNifti.Web.Features.Inventory;

/// <summary>One curated attribute a file carries, labelled as the metadata.json sidecar labels it.</summary>
public sealed record InspectorTagRow(string Name, string Keyword, string Value);

/// <summary>
/// The inspectors' DICOM tags: Core reads them (<see cref="DicomMetadataExtractor.ExtractFile"/>,
/// headers only), and this only decides which file Core may open. A file the scan recorded is read
/// only when it resolves under a configured data root again, through <see cref="DataRootResolver"/>,
/// so a snapshot whose roots changed cannot point the reader anywhere else.
/// </summary>
public sealed class InspectorTagReader
{
    private readonly DataRootResolver _resolver;
    private readonly RootPathPresenter _presenter;

    public InspectorTagReader(DataRootResolver resolver, RootPathPresenter presenter)
    {
        _resolver = resolver;
        _presenter = presenter;
    }

    /// <summary>
    /// The attributes of <paramref name="keywords"/> that the file has, in their order; absent ones
    /// are left out, so the tab shows what the file carries.
    /// </summary>
    /// <exception cref="PathRefusedException">The file is not under a data root, or not there.</exception>
    public Task<List<InspectorTagRow>> ReadAsync(string serverPath, IReadOnlyList<string> keywords)
    {
        var file = ResolveScannedFile(serverPath);
        return Task.Run(() =>
        {
            var values = DicomMetadataExtractor.ExtractFile(file.FullPath, keywords);
            var rows = new List<InspectorTagRow>();
            foreach (string keyword in keywords)
            {
                if (!values.TryGetValue(keyword, out object? value))
                    continue;
                string text = Format(value);
                if (text.Length > 0)
                    rows.Add(new InspectorTagRow(MetadataTagCatalog.FriendlyName(keyword), keyword, text));
            }

            return rows;
        });
    }

    /// <summary>A scanned file's server path as the resolver resolves it, by its root token.</summary>
    private ResolvedPath ResolveScannedFile(string serverPath)
    {
        string presented = _presenter.Present(serverPath);
        int colon = presented.IndexOf(':');
        if (presented == PathRedactor.OutsideRoots || colon <= 0
            || !_resolver.TryResolve(presented[..colon], presented[(colon + 1)..], RootAccess.Read, out var file, out string? refusal)
            || !DataRootResolver.FileExists(file))
        {
            throw new PathRefusedException("The file is no longer under a configured data root.");
        }

        return file;
    }

    /// <summary>Numbers invariantly, multi-valued tags joined with DICOM's backslash, null as empty.</summary>
    internal static string Format(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        IEnumerable sequence => string.Join("\\", sequence.Cast<object>().Select(Format)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
