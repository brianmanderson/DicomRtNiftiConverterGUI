using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// A test job: it logs a line, reports a step, then waits at its gate (named by
    /// <see cref="Name"/>) until the test opens it or the job's token is cancelled, and ends as
    /// <see cref="Outcome"/> says: <c>succeed</c>, <c>fail</c> (a failed outcome), <c>throw</c>
    /// (an exception carrying <see cref="Message"/>), or <c>fail-first</c> (throws on its first
    /// run only). A name starting with <c>auto</c> has no gate. <see cref="Project"/> is locked
    /// while it runs.
    /// </summary>
    internal sealed record GatedParameters(
        string Name,
        string Project = null,
        string Outcome = "succeed",
        string Message = null);

    /// <summary>The gates and run counts of the test jobs, shared by the handlers of one host.</summary>
    internal sealed class TestGates
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new ConcurrentDictionary<string, TaskCompletionSource>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new ConcurrentDictionary<string, TaskCompletionSource>();
        private readonly ConcurrentDictionary<string, int> _runs = new ConcurrentDictionary<string, int>();

        public Task WaitAsync(string name, CancellationToken ct) =>
            name.StartsWith("auto", StringComparison.Ordinal) ? Task.CompletedTask : Gate(_gates, name).Task.WaitAsync(ct);

        public void Release(string name) => Gate(_gates, name).TrySetResult();

        public int Runs(string name) => _runs.TryGetValue(name, out int runs) ? runs : 0;

        public bool HasStarted(string name) => Runs(name) > 0;

        public Task WaitStartedAsync(string name, int seconds = 20) => Gate(_started, name).Task.WaitAsync(TimeSpan.FromSeconds(seconds));

        internal int RecordRun(string name)
        {
            int run = _runs.AddOrUpdate(name, 1, (_, n) => n + 1);
            Gate(_started, name).TrySetResult();
            return run;
        }

        private static TaskCompletionSource Gate(ConcurrentDictionary<string, TaskCompletionSource> gates, string name) =>
            gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal abstract class GatedHandler : IJobHandler
    {
        private readonly TestGates _gates;

        protected GatedHandler(TestGates gates)
        {
            _gates = gates;
        }

        public abstract string Kind { get; }

        public abstract JobLane Lane { get; }

        public JobDescription Describe(JsonElement parameters)
        {
            var p = JobContext.ReadParameters<GatedParameters>(parameters);
            if (p.Name == "refuse")
                throw new JobRefusedException("Refused by the test handler.");
            return new JobDescription("Test " + p.Name, "DicomRtNifti.Cli --test " + p.Name, p.Project);
        }

        public async Task<JobOutcome> RunAsync(JobContext context)
        {
            var p = context.ReadParameters<GatedParameters>();
            int run = _gates.RecordRun(p.Name);
            context.Log.Report($"started {p.Name}");
            if (p.Message != null)
                context.Log.Report(p.Message);
            context.Report(new JobProgress("steps", 0, 2, true));

            await _gates.WaitAsync(p.Name, context.CancellationToken);
            context.Report(new JobProgress("steps", 1, 2, true));

            switch (p.Outcome)
            {
                case "fail":
                    return JobOutcome.Failed(p.Message ?? "failed by the test");
                case "throw":
                    throw new InvalidOperationException(p.Message ?? "thrown by the test");
                case "fail-first" when run == 1:
                    throw new InvalidOperationException(p.Message ?? "the first run fails");
                default:
                    return JobOutcome.Succeeded();
            }
        }
    }

    internal sealed class HeavyTestHandler : GatedHandler
    {
        public HeavyTestHandler(TestGates gates)
            : base(gates)
        {
        }

        public override string Kind => TestJobHandlers.Heavy;

        public override JobLane Lane => JobLane.Heavy;
    }

    internal sealed class LightTestHandler : GatedHandler
    {
        public LightTestHandler(TestGates gates)
            : base(gates)
        {
        }

        public override string Kind => TestJobHandlers.Light;

        public override JobLane Lane => JobLane.Light;
    }

    internal static class TestJobHandlers
    {
        public const string Heavy = "test-heavy";
        public const string Light = "test-light";

        public static void Add(IServiceCollection services)
        {
            services.AddSingleton<TestGates>();
            JobServices.AddHandler<HeavyTestHandler>(services);
            JobServices.AddHandler<LightTestHandler>(services);
        }
    }
}
