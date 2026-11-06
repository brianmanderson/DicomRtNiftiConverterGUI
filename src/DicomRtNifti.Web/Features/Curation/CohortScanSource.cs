using DicomRtNifti.Core.Models;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Features.Curation;

/// <summary>
/// Where ROI curation reads a cohort's scan: the latest <see cref="DicomScanResult"/> of a folder,
/// named as the browser names it (a data root and a root-relative path), or null when the server
/// holds none. The seam to Inventory's scan snapshot store (brief W4-G), which keeps the scans the
/// cohort pages share and will implement this interface; until it does, <see cref="RoiScanCache"/>
/// stands in, holding the scans curation's own <c>cohort-analyze</c> jobs read.
/// </summary>
public interface ICohortScanSource
{
    DicomScanResult? Find(string? rootId, string? relativePath);
}

/// <summary>
/// The stand-in <see cref="ICohortScanSource"/>: the scans the ROI-name analyses read, by folder,
/// newest few only. Server memory, never persisted, gone on restart (the page then offers to scan
/// again). A scan names patients, so it is held as the job log is: server-side, shown only to the
/// users who may see the cohort.
/// </summary>
public sealed class RoiScanCache : ICohortScanSource
{
    /// <summary>How many scans are kept; a cohort's scan is header data, but a large cohort's is not small.</summary>
    public const int Capacity = 4;

    private readonly DataRootResolver _resolver;
    private readonly LinkedList<(string Folder, DicomScanResult Scan)> _scans = new();

    public RoiScanCache(DataRootResolver resolver) => _resolver = resolver;

    /// <summary>Keeps <paramref name="scan"/> as the latest of <paramref name="folder"/>.</summary>
    public void Put(ResolvedPath folder, DicomScanResult scan)
    {
        lock (_scans)
        {
            for (var node = _scans.First; node is not null; node = node.Next)
            {
                if (node.Value.Folder == folder.Presented)
                {
                    _scans.Remove(node);
                    break;
                }
            }

            _scans.AddFirst((folder.Presented, scan));
            while (_scans.Count > Capacity)
                _scans.RemoveLast();
        }
    }

    public DicomScanResult? Find(string? rootId, string? relativePath)
    {
        if (rootId is null || !_resolver.TryResolve(rootId, relativePath, RootAccess.Read, out var folder, out _))
            return null;
        lock (_scans)
            return _scans.FirstOrDefault(s => s.Folder == folder.Presented).Scan;
    }
}
