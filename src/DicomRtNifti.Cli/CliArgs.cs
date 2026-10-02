using System;
using System.Linq;

namespace DicomRtNifti.Cli
{
    /// <summary>
    /// Shared argv helpers for the headless entry points.
    ///
    /// Matching is exact-string and case-insensitive, with no subcommand grammar: a mode is just
    /// a flag that happens to be present. That is why every mode name has to stay distinct —
    /// nothing prevents two modes being passed at once, the first check simply wins.
    /// </summary>
    internal static class CliArgs
    {
        /// <summary>True when <paramref name="flag"/> appears anywhere in <paramref name="args"/>.</summary>
        public static bool HasFlag(string[] args, string flag) =>
            args != null && args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the value following <paramref name="name"/>, or throws when it is absent.
        /// </summary>
        public static string RequireArg(string[] args, string name)
        {
            string value = OptionalArg(args, name);
            if (value == null)
                throw new CliUsageException($"Required argument '{name}' is missing.");
            return value;
        }

        /// <summary>
        /// Returns the value following <paramref name="name"/>, or null when the flag is absent
        /// or is the final token (so has no value after it).
        /// </summary>
        public static string OptionalArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>Returns the value following the first of <paramref name="names"/> that is present.</summary>
        public static string OptionalArgAny(string[] args, params string[] names)
        {
            foreach (var name in names)
            {
                string value = OptionalArg(args, name);
                if (value != null) return value;
            }
            return null;
        }
    }

    /// <summary>
    /// The command line cannot be acted on: a required flag is missing or malformed, or an input
    /// path it names does not exist. Raised only by the CLI's own argument checks, and the only
    /// exception <see cref="HeadlessRunner.Run"/> maps to exit 2. Classifying by origin rather
    /// than by exception type matters: an ArgumentOutOfRangeException from SimpleITK or a
    /// FileNotFoundException for a missing assembly is a conversion failure (exit 1, with its
    /// stack trace), not a typo on the command line.
    /// </summary>
    internal sealed class CliUsageException : Exception
    {
        public CliUsageException(string message) : base(message) { }
    }

    /// <summary>
    /// Writes progress straight to stderr. <see cref="Progress{T}"/> posts each report to the
    /// thread pool when there is no synchronization context, so lines could reach stderr after
    /// the command had returned, and out of order with the runner's own Console.Error lines.
    /// </summary>
    internal sealed class StderrProgress : IProgress<string>
    {
        private readonly string _prefix;

        public StderrProgress(string prefix = "") { _prefix = prefix ?? ""; }

        public void Report(string value) => Console.Error.WriteLine(_prefix + value);
    }
}
