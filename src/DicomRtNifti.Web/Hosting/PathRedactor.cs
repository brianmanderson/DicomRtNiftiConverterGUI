using System.Text;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// Rewrites text for the browser and the logs: a path under a configured folder becomes
/// <c>id:</c> plus the rest (<c>archive:2024\H&amp;N\ct.dcm</c>), and any other absolute path,
/// Windows, UNC, device or POSIX, becomes <see cref="OutsideRoots"/>. Core's messages carry
/// absolute paths (a scan's unreadable-file samples, a key-inside-export refusal, a native-load
/// failure), so everything shown or logged passes through here (plan sections 3.3 and 6).
///
/// Paths are found in free text, so the end of one is a judgement: a directory name may contain
/// spaces (it is followed by another separator, and does not end in whitespace), the last name
/// ends at whitespace, a quote, ':' or the end of the text, and trailing sentence punctuation is
/// kept outside. When in doubt the
/// scanner takes more text, never less: over-redaction costs a word of a message, under-redaction
/// leaks a server path.
/// </summary>
public sealed class PathRedactor
{
    public const string OutsideRoots = "<outside roots>";

    private static readonly StringComparison Comparison = DataRootResolver.PathComparison;

    /// <summary>A redactor that knows no folder: every absolute path becomes <see cref="OutsideRoots"/>.</summary>
    public static readonly PathRedactor NoRoots = new(Array.Empty<DataRoot>());

    // Longest path first, so a root nested in another is rewritten to its own id.
    private readonly (string Prefix, string Token)[] _prefixes;

    /// <summary>Rewrites every configured folder, browsable or not, to its id.</summary>
    public PathRedactor(DataRoots roots)
        : this(roots.AllFolders)
    {
    }

    private PathRedactor(IEnumerable<DataRoot> folders)
    {
        var prefixes = new List<(string Prefix, string Token)>();
        foreach (var folder in folders)
        {
            foreach (string spelling in Spellings(folder.Path))
                prefixes.Add((spelling, folder.Id + ":"));
        }

        _prefixes = prefixes.OrderByDescending(p => p.Prefix.Length).ToArray();
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        return RedactOutside(RewriteRoots(text));
    }

    private string RewriteRoots(string text)
    {
        if (_prefixes.Length == 0)
            return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int consumed = 0;
            string? token = null;
            if (i == 0 || !IsNameChar(text[i - 1]))
            {
                foreach (var (prefix, candidate) in _prefixes)
                {
                    if (string.Compare(text, i, prefix, 0, prefix.Length, Comparison) != 0)
                        continue;
                    int end = i + prefix.Length;
                    if (end == text.Length || !IsNameChar(text[end]) || IsSeparator(text[end]) || IsSeparator(prefix[^1]))
                    {
                        // Consume one separator after the root so "C:\data\archive\x" reads "archive:x".
                        consumed = prefix.Length + (end < text.Length && IsSeparator(text[end]) && !IsSeparator(prefix[^1]) ? 1 : 0);
                        token = candidate;
                        break;
                    }
                }
            }

            if (token is null)
            {
                sb.Append(text[i]);
                i++;
            }
            else
            {
                sb.Append(token);
                i += consumed;
            }
        }

        return sb.ToString();
    }

    private static string RedactOutside(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = AbsolutePathBodyStart(text, i);
            if (start < 0)
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            int end = ScanPathBody(text, start);
            sb.Append(OutsideRoots);
            i = end;
        }

        return sb.ToString();
    }

    /// <summary>
    /// If an absolute path starts at <paramref name="i"/>, the index just after its root
    /// (<c>C:\</c>, <c>\\</c>, <c>/</c>); otherwise -1.
    /// </summary>
    private static int AbsolutePathBodyStart(string text, int i)
    {
        char previous = i > 0 ? text[i - 1] : ' ';

        // Drive: C:\ or C:/, not inside a word ("e:\" in "archive:\x" is a root token, not a drive).
        if (IsDriveAt(text, i) && !char.IsLetterOrDigit(previous))
            return i + 3;

        if (i + 2 < text.Length && text[i] == '\\' && text[i + 1] == '\\' && !IsSeparator(previous))
        {
            // Device paths: \\?\C:\x, \\?\UNC\server\share, \\.\pipe\x.
            if (i + 3 < text.Length && text[i + 2] is '?' or '.' && text[i + 3] == '\\')
            {
                int j = i + 4;
                if (IsDriveAt(text, j))
                    return j + 3;
                if (string.Compare(text, j, @"UNC\", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                    return j + 4;
                return j;
            }

            // UNC: \\server\share.
            if (!IsSeparator(text[i + 2]) && !char.IsWhiteSpace(text[i + 2]))
                return i + 2;
        }

        // POSIX: a '/' that starts a token ("/srv/x", "=/srv/x", "'/srv/x'"); not "3/10", "and/or",
        // "http://host/x" or "archive:2024/x".
        if (text[i] == '/' && i + 1 < text.Length && IsNameChar(text[i + 1]) && StartsToken(previous))
            return i + 1;

        return -1;
    }

    private static bool StartsToken(char c) =>
        char.IsWhiteSpace(c) || c is '"' or '\'' or '(' or '[' or '{' or '<' or '=' or ',' or ';' or '>' or '`';

    private static int ScanPathBody(string text, int i)
    {
        while (i < text.Length)
        {
            int runEnd = i;
            while (runEnd < text.Length && !IsSeparator(text[runEnd]) && !IsHardStop(text[runEnd]))
                runEnd++;

            // A directory name, spaces and all; carry on after the separator. A name never ends in
            // whitespace, so "x --json-out /tmp" is a name and then the start of another path.
            if (runEnd < text.Length && IsSeparator(text[runEnd]) && (runEnd == i || !char.IsWhiteSpace(text[runEnd - 1])))
            {
                i = runEnd + 1;
                continue;
            }

            // The last name: up to the first whitespace, without trailing punctuation.
            int end = i;
            while (end < runEnd && !char.IsWhiteSpace(text[end]))
                end++;
            while (end > i && text[end - 1] is '.' or ',' or ';' or ')' or ']' or '}' or '\'' or '!')
                end--;
            return end;
        }

        return i;
    }

    private static bool IsDriveAt(string text, int i) =>
        i + 2 < text.Length && char.IsAsciiLetter(text[i]) && text[i + 1] == ':' && IsSeparator(text[i + 2]);

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static bool IsHardStop(char c) => c is '"' or '<' or '>' or '|' or '*' or '?' or ':' or '\r' or '\n' or '\t' || char.IsControl(c);

    private static bool IsNameChar(char c) => !char.IsWhiteSpace(c) && !IsHardStop(c) && c is not '\'' and not '(' and not ')' and not ',' and not ';' and not '=';

    /// <summary>The ways a folder's path appears in messages: as configured, with '/' on Windows, and in long-path form.</summary>
    private static IEnumerable<string> Spellings(string path)
    {
        var spellings = new HashSet<string>(StringComparer.FromComparison(Comparison)) { path };
        if (OperatingSystem.IsWindows())
        {
            spellings.Add(path.Replace('\\', '/'));
            spellings.Add(path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path);
        }

        return spellings;
    }
}
