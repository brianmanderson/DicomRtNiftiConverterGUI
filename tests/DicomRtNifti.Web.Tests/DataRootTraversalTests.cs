using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The path-traversal table (plan section 7.1): every way a browser-supplied path could name
    /// something outside its root, each of which <see cref="DataRootResolver"/> must refuse, and
    /// the ordinary names it must accept. Rows are keyed by label so test names stay printable
    /// (several inputs carry control characters). A row that cannot apply on this machine (a
    /// Windows-only name, a symlink without the privilege to create one) is left out of the
    /// data, not skipped; the per-lane counts in CI show what ran where.
    /// </summary>
    public sealed class DataRootTraversalTests : IClassFixture<TraversalTree>
    {
        private enum Os
        {
            Any,
            Windows,
            Posix,
        }

        private enum Needs
        {
            Nothing,
            Symlinks,
            Junctions,
        }

        private sealed class Row
        {
            public Row(string label, string relative, Os os = Os.Any, Needs needs = Needs.Nothing,
                string rootId = "archive", RootAccess access = RootAccess.Read, string expected = null)
            {
                Label = label;
                Relative = relative;
                RunsOn = os;
                Requires = needs;
                RootId = rootId;
                Access = access;
                Expected = expected;
            }

            public string Label { get; }
            public string Relative { get; }
            public Os RunsOn { get; }
            public Needs Requires { get; }
            public string RootId { get; }
            public RootAccess Access { get; }

            /// <summary>For an accepted row, the normalized relative path the resolver must return.</summary>
            public string Expected { get; }

            public bool AppliesHere =>
                (RunsOn == Os.Any || (RunsOn == Os.Windows) == OperatingSystem.IsWindows())
                && (Requires != Needs.Symlinks || Links.CanCreateSymlinks)
                && (Requires != Needs.Junctions || OperatingSystem.IsWindows());
        }

        private static readonly Row[] Refused =
        {
            // Parent segments, every separator style.
            new Row("parent", ".."),
            new Row("parent-slash", "../"),
            new Row("parent-backslash", "..\\"),
            new Row("parent-then-sibling", "../outside"),
            new Row("parent-then-sibling-backslash", "..\\outside"),
            new Row("parent-then-file", "../outside/secret.txt"),
            new Row("down-then-up-twice", "inside/../.."),
            new Row("down-then-up-out", "inside/../../outside"),
            new Row("down-then-up-out-backslash", "inside\\..\\..\\outside"),
            new Row("down-then-up-inside", "inside/../inside"),
            new Row("dot-then-parent", "./../outside"),
            new Row("dot-segment-inside-path", "inside/./../../outside"),
            new Row("double-separator-parent", "..//outside"),
            new Row("mixed-separators", "..\\/..\\/outside"),
            new Row("dot", "."),
            new Row("dot-prefix", "./inside"),
            new Row("three-dots", "..."),
            new Row("four-dots-then-sibling", "....//outside"),
            new Row("case-variant-parent", "INSIDE\\..\\..\\OUTSIDE"),

            // Absolute, rooted, drive, UNC and device inputs.
            new Row("posix-absolute", "/etc/passwd"),
            new Row("posix-root", "/"),
            new Row("backslash-root", "\\"),
            new Row("rooted-backslash", "\\Windows\\win.ini"),
            new Row("unc-forward", "//server/share/x"),
            new Row("unc-backslash", "\\\\server\\share\\x"),
            new Row("unc-mixed", "\\/server/share"),
            new Row("device-long-path", "\\\\?\\C:\\Windows"),
            new Row("device-dot", "\\\\.\\C:\\Windows"),
            new Row("device-unc", "\\\\?\\UNC\\server\\share\\x"),
            new Row("drive-absolute", "C:\\Windows"),
            new Row("drive-absolute-forward", "C:/Windows"),
            new Row("drive-only", "c:"),
            new Row("drive-relative", "C:Windows"),
            new Row("drive-relative-parent", "C:..\\outside"),
            new Row("other-drive", "Z:\\"),

            // Encodings a later decode would turn into traversal.
            new Row("percent-dots", "%2e%2e/outside"),
            new Row("percent-dots-and-slash-upper", "%2E%2E%2Foutside"),
            new Row("percent-slash", "..%2foutside"),
            new Row("percent-backslash", "..%5coutside"),
            new Row("percent-dots-backslash", "%2e%2e%5coutside"),
            new Row("double-percent", "%252e%252e/outside"),
            new Row("overlong-utf8-dots", "%c0%ae%c0%ae/outside"),
            new Row("percent-nul", "inside%00.txt"),
            new Row("fullwidth-dots", "\uFF0E\uFF0E/outside"),
            new Row("two-dot-leader", "\u2025/outside"),
            new Row("fullwidth-solidus", "inside\uFF0F..\uFF0F..\uFF0Foutside"),

            // Control characters.
            new Row("nul", "inside\0/x"),
            new Row("newline", "inside\n"),
            new Row("tab", "inside\t/x"),
            new Row("too-long", new string('a', DataRootResolver.MaxRelativePathLength + 1)),

            // Root ids: unknown, empty, case or whitespace variants, path-shaped.
            new Row("unknown-root", "inside", rootId: "nope"),
            new Row("empty-root", "inside", rootId: ""),
            new Row("null-root", "inside", rootId: null),
            new Row("root-case-variant", "inside", rootId: "ARCHIVE"),
            new Row("root-trailing-space", "inside", rootId: "archive "),
            new Row("root-path-shaped", "inside", rootId: "../archive"),
            new Row("server-folder-not-browsable", "jobs", rootId: "state"),
            new Row("write-to-read-only-root", "inside", access: RootAccess.ReadWrite),

            // Windows names: streams, devices, names Windows shortens, characters it forbids.
            new Row("ads-colon", "inside:secret", Os.Windows),
            new Row("ads-on-file", "inside/file.txt:stream", Os.Windows),
            new Row("ads-data", "inside/file.txt::$DATA", Os.Windows),
            new Row("ads-fullwidth-colon", "inside：stream", Os.Windows),
            new Row("device-con", "CON", Os.Windows),
            new Row("device-con-extension", "con.txt", Os.Windows),
            new Row("device-nul", "NUL", Os.Windows),
            new Row("device-aux-folder", "aux/x", Os.Windows),
            new Row("device-com1", "COM1", Os.Windows),
            new Row("device-lpt9-extension", "LPT9.log", Os.Windows),
            new Row("device-conin", "CONIN$", Os.Windows),
            new Row("device-com-superscript", "COM\u00B9", Os.Windows),
            new Row("device-trailing-space", "inside/PRN ", Os.Windows),
            new Row("parent-with-trailing-space", ".. /outside", Os.Windows),
            new Row("parent-with-trailing-dot-and-space", ".. . /outside", Os.Windows),
            new Row("trailing-dot", "inside.", Os.Windows),
            new Row("trailing-space", "inside ", Os.Windows),
            new Row("trailing-dot-space", "inside. ", Os.Windows),
            new Row("wildcard-star", "in*side", Os.Windows),
            new Row("wildcard-question", "in?side", Os.Windows),
            new Row("angle-brackets", "in<side>", Os.Windows),
            new Row("pipe", "in|side", Os.Windows),
            new Row("quote", "in\"side", Os.Windows),

            // Links whose final target is outside the root.
            new Row("symlink-out", "link-out", needs: Needs.Symlinks),
            new Row("symlink-out-file-below", "link-out/secret.txt", needs: Needs.Symlinks),
            new Row("symlink-to-prefix-sibling", "link-evil", needs: Needs.Symlinks),
            new Row("symlink-relative-out", "link-relative-out", needs: Needs.Symlinks),
            new Row("symlink-relative-out-file-below", "link-relative-out/secret.txt", needs: Needs.Symlinks),
            new Row("symlink-chain-out", "chain", needs: Needs.Symlinks),
            new Row("symlink-chain-out-file-below", "chain/secret.txt", needs: Needs.Symlinks),
            new Row("symlink-hop-out", "hop", needs: Needs.Symlinks),
            new Row("file-symlink-out", "file-link-out", needs: Needs.Symlinks),
            new Row("broken-symlink-out", "broken-out", needs: Needs.Symlinks),
            new Row("broken-symlink-out-below", "broken-out/x", needs: Needs.Symlinks),
            new Row("nested-symlink-out", "inside/deep-out", needs: Needs.Symlinks),
            new Row("nested-symlink-out-file-below", "inside/deep-out/secret.txt", needs: Needs.Symlinks),
            new Row("symlink-in-then-nested-out", "link-in/deep-out/secret.txt", needs: Needs.Symlinks),
            new Row("symlink-out-separator-variant", "link-out\\secret.txt", needs: Needs.Symlinks),
            new Row("junction-out", "junction-out", needs: Needs.Junctions),
            new Row("junction-out-file-below", "junction-out\\secret.txt", needs: Needs.Junctions),
            new Row("junction-to-prefix-sibling-case-variant", "junction-evil", needs: Needs.Junctions),
            new Row("junction-in-then-nested-symlink-out", "junction-in/deep-out", Os.Windows, Needs.Symlinks),
        };

        private static readonly Row[] Accepted =
        {
            new Row("root-itself", "", expected: ""),
            new Row("root-itself-null", null, expected: ""),
            new Row("child", "inside", expected: "inside"),
            new Row("file", "inside/file.txt", expected: "inside/file.txt"),
            new Row("backslash-separator", "inside\\file.txt", expected: "inside/file.txt"),
            new Row("doubled-separator", "inside//file.txt", expected: "inside/file.txt"),
            new Row("trailing-separator", "inside/", expected: "inside"),
            new Row("not-yet-created", "not-yet/created", expected: "not-yet/created"),
            new Row("spaces-and-punctuation", "H&N 2024/Smith, J (1)", expected: "H&N 2024/Smith, J (1)"),
            new Row("percent-sign", "100% dose", expected: "100% dose"),
            new Row("percent-encoded-space", "Patient%20A", expected: "Patient%20A"),
            new Row("non-ascii", "naïve/Ångström", expected: "naïve/Ångström"),
            new Row("dots-inside-a-name", "a..b/c...d", expected: "a..b/c...d"),
            new Row("leading-dot-name", ".hidden", expected: ".hidden"),
            new Row("device-like-but-longer", "CONSOLE/COM10", expected: "CONSOLE/COM10"),
            new Row("posix-trailing-dot", "Smith J.", Os.Posix, expected: "Smith J."),
            new Row("posix-colon", "12:30:00", Os.Posix, expected: "12:30:00"),
            new Row("posix-fullwidth-colon", "12：30", Os.Posix, expected: "12：30"),
            new Row("posix-device-name", "CON", Os.Posix, expected: "CON"),
            new Row("symlink-in", "link-in", needs: Needs.Symlinks, expected: "link-in"),
            new Row("symlink-in-file-below", "link-in/file.txt", needs: Needs.Symlinks, expected: "link-in/file.txt"),
            new Row("file-symlink-in", "file-link-in", needs: Needs.Symlinks, expected: "file-link-in"),
            new Row("broken-symlink-in", "broken-in", needs: Needs.Symlinks, expected: "broken-in"),
            new Row("junction-in", "junction-in", needs: Needs.Junctions, expected: "junction-in"),
            new Row("junction-in-file-below", "junction-in\\file.txt", needs: Needs.Junctions, expected: "junction-in/file.txt"),
            new Row("junction-case-variant-target", "junction-case/file.txt", needs: Needs.Junctions, expected: "junction-case/file.txt"),
        };

        private readonly TraversalTree _tree;

        public DataRootTraversalTests(TraversalTree tree)
        {
            _tree = tree;
        }

        public static IEnumerable<object[]> RefusedRows() =>
            Refused.Where(r => r.AppliesHere).Select(r => new object[] { r.Label });

        public static IEnumerable<object[]> AcceptedRows() =>
            Accepted.Where(r => r.AppliesHere).Select(r => new object[] { r.Label });

        [Theory]
        [MemberData(nameof(RefusedRows))]
        public void Refused_row(string label)
        {
            var row = Refused.Single(r => r.Label == label);

            bool ok = _tree.Resolver.TryResolve(row.RootId, row.Relative, row.Access, out var resolved, out string refusal);

            Assert.False(ok, $"{label}: resolved to {resolved?.FullPath}");
            Assert.Null(resolved);
            Assert.False(string.IsNullOrWhiteSpace(refusal));
            Assert.DoesNotContain(_tree.Base, refusal, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<PathRefusedException>(() => _tree.Resolver.Resolve(row.RootId, row.Relative, row.Access));
        }

        [Theory]
        [MemberData(nameof(AcceptedRows))]
        public void Accepted_row(string label)
        {
            var row = Accepted.Single(r => r.Label == label);

            bool ok = _tree.Resolver.TryResolve(row.RootId, row.Relative, row.Access, out var resolved, out string refusal);

            Assert.True(ok, $"{label}: refused ({refusal})");
            Assert.Equal(row.Expected, resolved.RelativePath);
            Assert.Equal("archive:" + row.Expected, resolved.Presented);
            Assert.True(DataRootResolver.IsWithin(resolved.FullPath, _tree.Roots.Find("archive").Path));
        }

        [Fact]
        public void No_link_row_is_dropped_where_links_can_be_made()
        {
            // POSIX can always create a symlink, and the CI runners can on Windows too, so there the
            // whole table runs; only a Windows desktop without Developer Mode drops the symlink rows.
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CI") == "true")
                Assert.True(Links.CanCreateSymlinks, "symlinks cannot be created, so the link rows did not run");
            Assert.All(Refused.Where(r => r.Requires == Needs.Nothing && r.RunsOn == Os.Any), r => Assert.True(r.AppliesHere));
        }

        [Fact]
        public void Listing_shows_only_subfolders_that_resolve()
        {
            var root = _tree.Resolver.Resolve("archive", "");

            var names = _tree.Resolver.ListSubfolders(root);

            Assert.Contains("inside", names);
            Assert.Contains("sub", names);
            foreach (string escaping in new[] { "link-out", "link-evil", "link-relative-out", "hop", "chain", "broken-out", "junction-out", "junction-evil" })
                Assert.DoesNotContain(escaping, names);
            if (Links.CanCreateSymlinks)
                Assert.Contains("link-in", names);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("junction-in", names);
                Assert.Contains("junction-case", names);
            }
        }

        [Fact]
        public void A_server_path_is_checked_as_strictly_as_a_browser_path()
        {
            var job = _tree.Resolver.ResolveServerPath(ServerFolder.State, "jobs/0123abcd/job.json");
            Assert.Equal("state:jobs/0123abcd/job.json", job.Presented);
            Assert.True(DataRootResolver.IsWithin(job.FullPath, _tree.State));

            Assert.Throws<PathRefusedException>(() => _tree.Resolver.ResolveServerPath(ServerFolder.State, "../outside"));
            Assert.Throws<PathRefusedException>(() => _tree.Resolver.ResolveServerPath(ServerFolder.State, "/etc/passwd"));
            Assert.Throws<InvalidOperationException>(() => _tree.Resolver.ResolveServerPath(ServerFolder.Keys, "x"));
        }

        [Fact]
        public void A_resolved_path_prints_as_its_token()
        {
            var file = _tree.Resolver.Resolve("archive", "inside/file.txt");

            Assert.Equal("archive:inside/file.txt", file.ToString());
            Assert.Equal("archive:inside/file.txt", $"{file}");
            Assert.True(DataRootResolver.FileExists(file));
            Assert.True(DataRootResolver.DirectoryExists(_tree.Resolver.Resolve("archive", "inside")));
        }
    }
}
