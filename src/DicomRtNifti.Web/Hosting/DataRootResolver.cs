using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// A path that <see cref="DataRootResolver"/> accepted. Only the resolver creates one, so a method
/// taking a <see cref="ResolvedPath"/> cannot be handed a path that skipped the checks.
/// </summary>
public sealed class ResolvedPath
{
    internal ResolvedPath(DataRoot root, string relativePath, string fullPath)
    {
        Root = root;
        RelativePath = relativePath;
        FullPath = fullPath;
    }

    public DataRoot Root { get; }

    /// <summary>'/'-separated, no leading or trailing separator; empty for the root itself.</summary>
    public string RelativePath { get; }

    /// <summary>The server path. Never send it to the browser or a log; use <see cref="Presented"/>.</summary>
    public string FullPath { get; }

    /// <summary>What the browser sees: <c>archive:2024/H&amp;N</c>.</summary>
    public string Presented => Root.Id + ":" + RelativePath;

    /// <summary>The presented form, so an accidental interpolation shows the token rather than the path.</summary>
    public override string ToString() => Presented;
}

/// <summary>Thrown when a path is refused. The message says why and never echoes the input.</summary>
public sealed class PathRefusedException : Exception
{
    public PathRefusedException(string reason)
        : base(reason)
    {
    }
}

public enum RootStatus
{
    Available,
    Missing,
    Unreadable,
    NotResponding,
}

public sealed record RootAvailability(RootStatus Status, long? FreeBytes);

/// <summary>
/// The only code that turns <c>(rootId, relativePath)</c> into a filesystem path, and the only
/// web code that touches the filesystem (an architecture test enforces both). It refuses, on
/// every OS: absolute and rooted inputs, drive letters, UNC and <c>\\?\</c> or <c>\\.\</c>
/// prefixes, any '.' or '..' segment, control characters, percent-encoded path syntax, segments
/// whose Unicode compatibility form is path syntax, and any path in which a symlink or junction
/// leads outside the root. On Windows it also refuses ':' (drive or alternate data stream),
/// device names, names Windows would silently shorten (a trailing dot or space) and characters
/// Windows does not allow in a name. '/' and '\' separate segments on every OS. Comparison is
/// ordinal and case-insensitive on Windows, ordinal elsewhere.
///
/// Residual exposure: a link swapped between the check and the use (a race with someone who can
/// already write inside the root) is not prevented.
/// </summary>
public sealed partial class DataRootResolver
{
    public const int MaxRelativePathLength = 1024;

    private const int MaxLinkDepth = 32;

    private static readonly bool OnWindows = OperatingSystem.IsWindows();
    private static readonly char[] Separators = { '/', '\\' };
    private static readonly char[] SeparatorsAndColon = { '/', '\\', ':' };
    private static readonly char[] WindowsInvalidNameChars = { '<', '>', '"', '|', '?', '*' };

    private readonly DataRoots _roots;

    public DataRootResolver(DataRoots roots)
    {
        _roots = roots;
    }

    /// <summary>How paths compare on this OS.</summary>
    public static StringComparison PathComparison => OnWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"%(?:2e|2f|5c|25|00|3a)|%c0%(?:ae|af)|%c1%(?:9c|1c)|%e0%80%(?:ae|af)", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedPathSyntax();

    /// <summary>Resolves a path the browser named, or throws <see cref="PathRefusedException"/>.</summary>
    public ResolvedPath Resolve(string? rootId, string? relativePath, RootAccess access = RootAccess.Read)
    {
        if (!TryResolve(rootId, relativePath, access, out var resolved, out var refusal))
            throw new PathRefusedException(refusal);
        return resolved;
    }

    /// <summary>
    /// Resolves a path the browser named. <paramref name="access"/> is what the caller will do:
    /// asking for <see cref="RootAccess.ReadWrite"/> on a read-only root is refused.
    /// </summary>
    public bool TryResolve(
        string? rootId,
        string? relativePath,
        RootAccess access,
        [NotNullWhen(true)] out ResolvedPath? resolved,
        [NotNullWhen(false)] out string? refusal)
    {
        resolved = null;
        var root = _roots.Find(rootId);
        if (root is null)
        {
            refusal = "There is no data root with that id.";
            return false;
        }

        if (access == RootAccess.ReadWrite && root.Access != RootAccess.ReadWrite)
        {
            refusal = $"Root '{root.Id}' is read-only.";
            return false;
        }

        return TryResolveUnder(root, relativePath, out resolved, out refusal);
    }

    /// <summary>
    /// Resolves a server-generated path under a server folder (job state, keys, uploads). The
    /// same checks apply, so a mistake in the caller cannot leave the folder either.
    /// </summary>
    public ResolvedPath ResolveServerPath(ServerFolder folder, string relativePath)
    {
        var root = _roots.Folder(folder)
            ?? throw new InvalidOperationException($"The {folder} folder is not configured.");
        if (!TryResolveUnder(root, relativePath, out var resolved, out var refusal))
            throw new PathRefusedException(refusal);
        return resolved;
    }

    /// <summary>The sub-folders of a resolved folder that themselves resolve, by name.</summary>
    public IReadOnlyList<string> ListSubfolders(ResolvedPath folder)
    {
        var names = new List<string>();
        foreach (string child in Directory.EnumerateDirectories(folder.FullPath))
        {
            string name = Path.GetFileName(child);
            string relative = folder.RelativePath.Length == 0 ? name : folder.RelativePath + "/" + name;
            if (TryResolveUnder(folder.Root, relative, out _, out _))
                names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static bool DirectoryExists(ResolvedPath path) => Directory.Exists(path.FullPath);

    public static bool FileExists(ResolvedPath path) => File.Exists(path.FullPath);

    /// <summary>Whether a root can be listed, and its free space, giving up after <paramref name="timeout"/>.</summary>
    public static async Task<RootAvailability> CheckAvailabilityAsync(
        DataRoot root, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // A root on an unreachable share can block for tens of seconds; never hold a page on it.
        var check = Task.Run(() => CheckAvailability(root), cancellationToken);
        try
        {
            return await check.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new RootAvailability(RootStatus.NotResponding, null);
        }
    }

    public static RootAvailability CheckAvailability(DataRoot root)
    {
        try
        {
            if (!Directory.Exists(root.Path))
                return new RootAvailability(RootStatus.Missing, null);
            using var entries = Directory.EnumerateFileSystemEntries(root.Path).GetEnumerator();
            entries.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RootAvailability(RootStatus.Unreadable, null);
        }

        return new RootAvailability(RootStatus.Available, FreeBytes(root.Path));
    }

    /// <summary>Whether a configured root path is acceptable: fully qualified, no device prefix.</summary>
    public static bool IsUsableRootPath([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        return Path.IsPathFullyQualified(path);
    }

    /// <summary>A configured root path, normalized and without a trailing separator.</summary>
    public static string NormalizeRootPath(string path)
    {
        if (!IsUsableRootPath(path))
            throw new ArgumentException("A root path must be fully qualified.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Whether two configured root paths are the same folder or one contains the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        string x = NormalizeRootPath(a);
        string y = NormalizeRootPath(b);
        return IsWithin(x, y) || IsWithin(y, x);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it, on segment boundaries.</summary>
    public static bool IsWithin(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        root = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(root, PathComparison))
            return true;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool TryResolveUnder(
        DataRoot root,
        string? relativePath,
        [NotNullWhen(true)] out ResolvedPath? resolved,
        [NotNullWhen(false)] out string? refusal)
    {
        resolved = null;
        if (!TrySplit(relativePath ?? "", out var segments, out refusal))
            return false;
        if (!TryCheckLinks(root.Path, segments, out refusal))
            return false;

        string full = segments.Count == 0
            ? root.Path
            : Path.Join(root.Path, string.Join(Path.DirectorySeparatorChar, segments));
        resolved = new ResolvedPath(root, string.Join('/', segments), full);
        return true;
    }

    private static bool TrySplit(string relative, out List<string> segments, [NotNullWhen(false)] out string? refusal)
    {
        segments = new List<string>();
        refusal = null;

        if (relative.Length > MaxRelativePathLength)
            refusal = "The path is too long.";
        else if (relative.Any(char.IsControl))
            refusal = "A control character is refused.";
        else if (relative.Length > 0 && (relative[0] == '/' || relative[0] == '\\'))
            refusal = "The path must be relative to its root: absolute, rooted, UNC and device paths are refused.";
        else if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
            refusal = "A drive letter is refused.";
        else if (OnWindows && relative.Contains(':'))
            refusal = "':' is refused (a drive or an alternate data stream).";
        else if (EncodedPathSyntax().IsMatch(relative))
            refusal = "Percent-encoded path syntax is refused.";
        if (refusal is not null)
            return false;

        foreach (string segment in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            refusal = SegmentRefusal(segment);
            if (refusal is not null)
                return false;
            segments.Add(segment);
        }

        return true;
    }

    private static string? SegmentRefusal(string segment)
    {
        if (IsDotsOnly(segment))
            return "'.' and '..' segments are refused.";

        string compatible = segment.Normalize(NormalizationForm.FormKC);
        if (IsDotsOnly(compatible) || compatible.IndexOfAny(SeparatorsAndColon) >= 0)
            return "A name whose Unicode compatibility form is path syntax is refused.";

        if (!OnWindows)
            return null;
        if (segment[^1] == '.' || segment[^1] == ' ')
            return "A name ending in a dot or a space is refused (Windows drops them).";
        if (segment.IndexOfAny(WindowsInvalidNameChars) >= 0)
            return "A character Windows does not allow in a name is refused.";
        if (IsWindowsDeviceName(segment))
            return "A Windows device name is refused.";
        return null;
    }

    private static bool IsDotsOnly(string segment) => segment.Length > 0 && segment.All(c => c == '.');

    private static bool IsWindowsDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            return true;
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3');
    }

    /// <summary>
    /// Walks the segments from the root's real location; wherever an entry is a symlink or a
    /// junction, its final target must still be inside the root's real location.
    /// </summary>
    private static bool TryCheckLinks(string rootPath, List<string> segments, [NotNullWhen(false)] out string? refusal)
    {
        refusal = null;
        string realRoot;
        try
        {
            realRoot = RealPath(rootPath, MaxLinkDepth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathRefusedException)
        {
            refusal = "The root cannot be checked.";
            return false;
        }

        string current = realRoot;
        foreach (string segment in segments)
        {
            string next = Path.Join(current, segment);
            try
            {
                var (exists, link) = Inspect(next);
                if (!exists)
                    return true; // nothing below a missing entry can be a link
                if (link is not null)
                {
                    next = RealPath(FinalTarget(link), MaxLinkDepth);
                    if (!IsWithin(next, realRoot))
                    {
                        refusal = "A link in the path leads outside its root.";
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathRefusedException)
            {
                refusal = "A link in the path cannot be followed.";
                return false;
            }

            current = next;
        }

        return true;
    }

    /// <summary>
    /// The path with every symlink and junction on it replaced by its final target, and on
    /// Windows every name in its on-disk form (so a short 8.3 name and its long name compare equal).
    /// </summary>
    private static string RealPath(string path, int depth)
    {
        if (depth < 0)
            throw new PathRefusedException("Too many links.");

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? throw new PathRefusedException("The path has no root.");
        string[] parts = full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        string current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Join(current, parts[i]);
            var (exists, link) = Inspect(next);
            if (!exists)
                return Path.Join(current, string.Join(Path.DirectorySeparatorChar, parts[i..]));

            current = link is not null
                ? RealPath(FinalTarget(link), depth - 1)
                : Path.Join(current, OnDiskName(current, parts[i]));
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>Whether the entry exists (a broken link exists), and the entry itself when it is a link.</summary>
    private static (bool Exists, FileSystemInfo? Link) Inspect(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (false, null);
        }

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            return (true, null);

        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
        // A reparse point that is not a link (a cloud placeholder, a deduplicated file) is an ordinary entry.
        return (true, info.LinkTarget is null ? null : info);
    }

    private static string FinalTarget(FileSystemInfo link) =>
        (link.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new PathRefusedException("The link has no target.")).FullName;

    private static string OnDiskName(string parent, string name)
    {
        if (!OnWindows)
            return name;
        try
        {
            // FindFirstFile matches a short 8.3 name too and returns the entry's long name.
            return new DirectoryInfo(parent).EnumerateFileSystemInfos(name).FirstOrDefault()?.Name ?? name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return name;
        }
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
