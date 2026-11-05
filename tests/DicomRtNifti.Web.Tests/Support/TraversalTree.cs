using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DicomRtNifti.Web.Hosting;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// A data root with neighbours to escape to, and links that try. Built under the system temp
    /// folder on purpose: on macOS that is behind the /var -> /private/var symlink and on a
    /// Windows runner it can carry an 8.3 short name, both of which the resolver must see through.
    ///
    /// <code>
    /// base/
    ///   root/                  the data root "archive"
    ///     inside/file.txt
    ///     sub/
    ///     link-in -> root/inside             link-out -> outside       link-evil -> root-evil
    ///     link-relative-out -> ../outside    hop -> outside            chain -> root/hop
    ///     file-link-in -> inside/file.txt    file-link-out -> outside/secret.txt
    ///     broken-in -> root/not-there        broken-out -> base/nowhere
    ///     inside/deep-out -> outside
    ///     junction-in, junction-case, junction-out, junction-evil     (Windows)
    ///   root-evil/             a sibling that shares the root's name as a prefix
    ///   outside/secret.txt
    ///   state/                 the server's StateRoot
    /// </code>
    /// </summary>
    public sealed class TraversalTree : IDisposable
    {
        public TraversalTree()
        {
            Base = Path.Combine(Path.GetTempPath(), "dicomrtnifti-web-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            Root = Path.Combine(Base, "root");
            Outside = Path.Combine(Base, "outside");
            Evil = Path.Combine(Base, "root-evil");
            State = Path.Combine(Base, "state");

            Directory.CreateDirectory(Path.Combine(Root, "inside"));
            Directory.CreateDirectory(Path.Combine(Root, "sub"));
            File.WriteAllText(Path.Combine(Root, "inside", "file.txt"), "inside");
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(Outside, "secret.txt"), "secret");
            Directory.CreateDirectory(Evil);
            Directory.CreateDirectory(State);

            if (Links.CanCreateSymlinks)
            {
                Directory.CreateSymbolicLink(Path.Combine(Root, "link-in"), Path.Combine(Root, "inside"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "link-out"), Outside);
                Directory.CreateSymbolicLink(Path.Combine(Root, "link-evil"), Evil);
                Directory.CreateSymbolicLink(Path.Combine(Root, "link-relative-out"), Path.Combine("..", "outside"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "hop"), Outside);
                Directory.CreateSymbolicLink(Path.Combine(Root, "chain"), Path.Combine(Root, "hop"));
                File.CreateSymbolicLink(Path.Combine(Root, "file-link-in"), Path.Combine(Root, "inside", "file.txt"));
                File.CreateSymbolicLink(Path.Combine(Root, "file-link-out"), Path.Combine(Outside, "secret.txt"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "broken-in"), Path.Combine(Root, "not-there"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "broken-out"), Path.Combine(Base, "nowhere"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "inside", "deep-out"), Outside);
            }

            if (OperatingSystem.IsWindows())
            {
                Links.CreateJunction(Path.Combine(Root, "junction-in"), Path.Combine(Root, "inside"));
                Links.CreateJunction(Path.Combine(Root, "junction-case"), Path.Combine(Root.ToUpperInvariant(), "INSIDE"));
                Links.CreateJunction(Path.Combine(Root, "junction-out"), Outside);
                Links.CreateJunction(Path.Combine(Root, "junction-evil"), Evil.ToUpperInvariant());
            }

            Roots = DataRoots.From(new WebOptions
            {
                DataRoots =
                {
                    new DataRootOptions { Id = "archive", Path = Root, Access = RootAccess.Read },
                },
                StateRoot = State,
            });
            Resolver = new DataRootResolver(Roots);
        }

        public string Base { get; }

        public string Root { get; }

        public string Outside { get; }

        public string Evil { get; }

        public string State { get; }

        public DataRoots Roots { get; }

        public DataRootResolver Resolver { get; }

        public void Dispose()
        {
            try
            {
                // Remove every link first, so the recursive delete can never reach a target.
                foreach (string entry in Directory.EnumerateFileSystemEntries(Base, "*", SearchOption.AllDirectories)
                    .OrderByDescending(e => e.Length).ToList())
                {
                    var info = Directory.Exists(entry) ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
                    if (info.LinkTarget != null)
                        info.Delete();
                }

                Directory.Delete(Base, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>What this machine can create.</summary>
    public static class Links
    {
        private static readonly Lazy<bool> Symlinks = new Lazy<bool>(Probe);

        /// <summary>False on a Windows account without Developer Mode or the symlink privilege.</summary>
        public static bool CanCreateSymlinks => Symlinks.Value;

        public static void CreateJunction(string link, string target)
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start);
            string error = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("mklink /J failed: " + error);
        }

        private static bool Probe()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dicomrtnifti-web-tests", "probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(dir, "link"), dir);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                try
                {
                    string link = Path.Combine(dir, "link");
                    if (Directory.Exists(link))
                        Directory.Delete(link);
                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
