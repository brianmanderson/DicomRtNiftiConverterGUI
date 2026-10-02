using System;
using System.IO;
using DicomRtNifti.Cli;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Runs <see cref="HeadlessRunner.Run"/> in-process with stdout and stderr captured, so the
    /// exit-code and stderr contract is tested against the same assembly the gate publishes.
    /// Console redirection is process-wide, hence the collection: every test class that goes
    /// through here shares it and xunit runs them one at a time.
    /// </summary>
    public sealed class CliRun
    {
        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }

        private CliRun(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public static CliRun Execute(params string[] args)
        {
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                int code = HeadlessRunner.Run(args);
                return new CliRun(code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }

        public override string ToString() =>
            $"exit {ExitCode}\n--- stdout ---\n{Stdout}\n--- stderr ---\n{Stderr}";
    }

    [CollectionDefinition(CliConsoleCollection.Name, DisableParallelization = true)]
    public class CliConsoleCollection
    {
        public const string Name = "cli-console";
    }
}
