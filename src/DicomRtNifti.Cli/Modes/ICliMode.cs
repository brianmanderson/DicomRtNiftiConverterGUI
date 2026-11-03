using System;
using System.Collections.Generic;
using System.Linq;

namespace DicomRtNifti.Cli.Modes
{
    /// <summary>
    /// One headless mode: the flag that selects it, where it sits among the others, its block of
    /// --help, and its entry point. Each mode is one file in this folder, found at startup
    /// (<see cref="CliModes"/>), so adding a mode is adding a file: dispatch, the usage text and
    /// the "requires one of" message all pick it up (D-43).
    /// </summary>
    internal interface ICliMode
    {
        /// <summary>The flag that selects this mode, e.g. "--forward".</summary>
        string Flag { get; }

        /// <summary>
        /// Position in dispatch and in --help, ascending. A command line naming several modes runs
        /// the first, so the order is part of the contract: the four single-series modes come
        /// before the cohort modes, so their older "# rt_mask_validation" output can never be
        /// shadowed by a cohort flag.
        /// </summary>
        int Order { get; }

        /// <summary>This mode's block of --help, one entry per line as printed.</summary>
        IReadOnlyList<string> Usage { get; }

        /// <summary>Runs the mode; returns the exit code. Usage errors throw <see cref="Core.CommandLine.CommandLineUsageException"/>.</summary>
        int Run(string[] args);
    }

    /// <summary>The modes compiled into this assembly, discovered once, in <see cref="ICliMode.Order"/>.</summary>
    internal static class CliModes
    {
        private static readonly Lazy<IReadOnlyList<ICliMode>> Discovered = new Lazy<IReadOnlyList<ICliMode>>(Discover);

        public static IReadOnlyList<ICliMode> All => Discovered.Value;

        /// <summary>"--forward, --reverse, ..., or --cohort-convert": every mode's flag, in order.</summary>
        public static string DescribeFlags()
        {
            var flags = All.Select(m => m.Flag).ToList();
            if (flags.Count < 2)
                return string.Join("", flags);
            return string.Join(", ", flags.Take(flags.Count - 1)) + ", or " + flags[flags.Count - 1];
        }

        private static IReadOnlyList<ICliMode> Discover()
        {
            var modes = typeof(ICliMode).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ICliMode).IsAssignableFrom(t))
                .Select(t => (ICliMode)Activator.CreateInstance(t))
                .OrderBy(m => m.Order)
                .ToList();

            // Two modes sharing a flag or a position would make dispatch depend on reflection
            // order, which the runtime does not promise.
            var clash = modes.GroupBy(m => m.Order).FirstOrDefault(g => g.Count() > 1)?.Key.ToString()
                ?? modes.GroupBy(m => m.Flag, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
            if (clash != null)
                throw new InvalidOperationException($"Two CLI modes share the flag or order '{clash}'.");
            return modes;
        }
    }
}
