using System;
using DicomRtNifti.Core.CommandLine;

namespace DicomRtNifti.Cli
{
    /// <summary>
    /// Shared argv helpers for the headless entry points: aliases over Core's
    /// <see cref="CommandLineArgs"/>, which the cohort grammar uses too, so every mode matches
    /// flags the same way (exact string, case-insensitive, no subcommand grammar).
    /// </summary>
    internal static class CliArgs
    {
        /// <summary>True when <paramref name="flag"/> appears anywhere in <paramref name="args"/>.</summary>
        public static bool HasFlag(string[] args, string flag) => CommandLineArgs.HasFlag(args, flag);

        /// <summary>
        /// Returns the value following <paramref name="name"/>, or throws a usage error when it is absent.
        /// </summary>
        public static string RequireArg(string[] args, string name) => CommandLineArgs.RequireArg(args, name);

        /// <summary>
        /// Returns the value following <paramref name="name"/>, or null when the flag is absent
        /// or is the final token (so has no value after it).
        /// </summary>
        public static string OptionalArg(string[] args, string name) => CommandLineArgs.OptionalArg(args, name);

        /// <summary>Returns the value following the first of <paramref name="names"/> that is present.</summary>
        public static string OptionalArgAny(string[] args, params string[] names) =>
            CommandLineArgs.OptionalArgAny(args, names);
    }

    /// <summary>
    /// The command line cannot be acted on: a required flag is missing or malformed, or an input
    /// path it names does not exist. Raised only by the CLI's own argument checks; with Core's
    /// <see cref="CommandLineUsageException"/>, which it extends and which the cohort grammar
    /// raises, the only exception <see cref="HeadlessRunner.Run"/> maps to exit 2. Classifying by
    /// origin rather than by exception type matters: an ArgumentOutOfRangeException from
    /// SimpleITK or a FileNotFoundException for a missing assembly is a conversion failure (exit
    /// 1, with its stack trace), not a typo on the command line.
    /// </summary>
    internal sealed class CliUsageException : CommandLineUsageException
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
