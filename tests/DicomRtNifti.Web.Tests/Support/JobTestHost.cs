using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Tests;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// The job service on its own, in a generic host: the registrations <see cref="JobServices"/>
    /// makes, real folders under a temporary directory (a data root <c>archive</c>, the output, key
    /// and state roots), the test job kinds of <see cref="TestJobHandlers"/>, and every log line
    /// captured raw: no redacting logger factory, so a test of what this code logs sees exactly
    /// what it wrote. Two hosts over the same folders model a restart.
    /// </summary>
    internal sealed class JobTestHost : IAsyncDisposable
    {
        private readonly IHost _host;
        private bool _stopped;

        private JobTestHost(IHost host, JobFolders folders, CapturingLoggerProvider logs)
        {
            _host = host;
            Folders = folders;
            Logs = logs;
        }

        public JobFolders Folders { get; }

        public CapturingLoggerProvider Logs { get; }

        public IServiceProvider Services => _host.Services;

        public JobQueue Queue => Services.GetRequiredService<JobQueue>();

        public JobEvents Events => Services.GetRequiredService<JobEvents>();

        public TestGates Gates => Services.GetRequiredService<TestGates>();

        public static async Task<JobTestHost> StartAsync(
            JobFolders folders = null,
            Action<WebOptions> configure = null,
            Action<IServiceCollection> services = null)
        {
            folders ??= JobFolders.Create();
            var options = folders.Options();
            configure?.Invoke(options);

            var logs = new CapturingLoggerProvider();
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(Options.Create(options));
            builder.Services.AddSingleton<DataRoots>();
            builder.Services.AddSingleton<DataRootResolver>();
            builder.Services.AddSingleton<PathRedactor>();
            JobServices.Add(builder.Services);
            TestJobHandlers.Add(builder.Services);
            services?.Invoke(builder.Services);

            var host = builder.Build();
            await host.StartAsync();
            return new JobTestHost(host, folders, logs);
        }

        /// <summary>Stops the host as a service manager would: running jobs end as interrupted.</summary>
        public async Task StopAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            await _host.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _host.Dispose();
        }

        public JobRecord Submit(string kind, object parameters)
        {
            var submission = Queue.Submit(kind, parameters, "local:tester");
            if (!submission.Accepted)
                throw new InvalidOperationException("Refused: " + submission.Refusal);
            return submission.Job;
        }

        /// <summary>Polls the job until <paramref name="until"/> holds, or fails the test.</summary>
        public async Task<JobRecord> WaitAsync(string jobId, Func<JobRecord, bool> until, int seconds = 20)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var job = Queue.Find(jobId);
                if (job != null && until(job))
                    return job;
                if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
                    throw new TimeoutException($"Job {jobId} is {job?.State} ({job?.Reason}) after {seconds} s.");
                await Task.Delay(20);
            }
        }

        public Task<JobRecord> WaitForStateAsync(string jobId, JobState state, int seconds = 20) =>
            WaitAsync(jobId, j => j.State == state, seconds);

        public Task<JobRecord> WaitFinishedAsync(string jobId, int seconds = 20) =>
            WaitAsync(jobId, j => j.State.IsFinished(), seconds);

        /// <summary>The job's log as it is on disk.</summary>
        public string LogFile(string jobId)
        {
            string path = Path.Combine(Folders.State, "jobs", jobId, JobStore.LogFile);
            return File.Exists(path) ? ReadShared(path) : "";
        }

        /// <summary>Every audit line on disk.</summary>
        public string AuditFiles()
        {
            string dir = Path.Combine(Folders.State, "audit");
            return Directory.Exists(dir)
                ? string.Join("\n", Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).Select(ReadShared))
                : "";
        }

        public static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>The folders one or more test hosts share; deleted on dispose.</summary>
    internal sealed class JobFolders : IDisposable
    {
        private JobFolders(string root)
        {
            Root = root;
            Data = Path.Combine(root, "data");
            Output = Path.Combine(root, "output");
            Keys = Path.Combine(root, "keys");
            State = Path.Combine(root, "state");
            foreach (string dir in new[] { Data, Output, Keys, State })
                Directory.CreateDirectory(dir);
        }

        public string Root { get; }

        /// <summary>The data root <c>archive</c>.</summary>
        public string Data { get; }

        public string Output { get; }

        public string Keys { get; }

        public string State { get; }

        public static JobFolders Create() => new JobFolders(DicomTestData.NewTempDir());

        public WebOptions Options() => new WebOptions
        {
            DataRoots = { new DataRootOptions { Id = "archive", Path = Data, Access = RootAccess.Read } },
            OutputRoot = Output,
            KeyRoot = Keys,
            StateRoot = State,
        };

        /// <summary>
        /// A copy of every folder, read while the files are still open, as a process killed now
        /// would leave them on disk: a second host over the copy is that process's restart.
        /// </summary>
        public JobFolders SnapshotAsKilled()
        {
            var copy = new JobFolders(DicomTestData.NewTempDir());
            CopyTree(Data, copy.Data);
            CopyTree(Output, copy.Output);
            CopyTree(Keys, copy.Keys);
            CopyTree(State, copy.State);
            return copy;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                // Open as the live process allows: its lock files and log are open for writing.
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = File.Create(Path.Combine(to, Path.GetRelativePath(from, file)));
                source.CopyTo(target);
            }
        }
    }

    /// <summary>A process that is alive for the test's duration and is not this one.</summary>
    internal sealed class OtherLiveProcess : IDisposable
    {
        private readonly Process _process;

        public OtherLiveProcess()
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > nul")
                : new ProcessStartInfo("sleep", "60");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            _process = Process.Start(start);
        }

        public int Id => _process.Id;

        public void Dispose()
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            _process.Dispose();
        }

        /// <summary>The id of a process that has exited.</summary>
        public static int ExitedProcessId()
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c exit 0")
                : new ProcessStartInfo("true");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using var process = Process.Start(start);
            process.WaitForExit();
            return process.Id;
        }
    }
}
