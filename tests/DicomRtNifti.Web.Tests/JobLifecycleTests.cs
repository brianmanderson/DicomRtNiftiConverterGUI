using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The job service's lifecycle (plan sections 3.2 and 7.1): a job is queued, runs on its
    /// lane's worker and ends as succeeded, failed or cancelled, each transition on disk and in
    /// the audit log; the heavy lane runs one job at a time while the light lane runs beside it; a
    /// full lane refuses; cancel and retry do what the Jobs page promises.
    /// </summary>
    public sealed class JobLifecycleTests
    {
        [Fact]
        public async Task A_job_runs_to_success_and_every_transition_is_recorded()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("a"));
            Assert.Equal(JobState.Queued, job.State);
            Assert.Equal(JobLane.Heavy, job.Lane);
            Assert.Equal("Test a", job.Title);
            Assert.Matches("^[0-9a-f]{32}$", job.Id);

            var running = await host.WaitForStateAsync(job.Id, JobState.Running);
            Assert.NotNull(running.StartedUtc);
            Assert.Equal(JobState.Running, OnDisk(host, job.Id).State);

            host.Gates.Release("a");
            var done = await host.WaitForStateAsync(job.Id, JobState.Succeeded);

            Assert.NotNull(done.FinishedUtc);
            Assert.Equal(1, done.Attempt);
            Assert.Equal(new JobProgress("steps", 1, 2, true), done.Progress);
            var file = OnDisk(host, job.Id);
            Assert.Equal(JobState.Succeeded, file.State);
            Assert.Equal(done.FinishedUtc, file.FinishedUtc);

            string log = host.LogFile(job.Id);
            Assert.Contains("started a", log);
            Assert.EndsWith("Job succeeded.\n", log);

            string audit = host.AuditFiles();
            foreach (string action in new[] { "job.submit", "job.start", "job.finish" })
                Assert.Contains($"\"action\":\"{action}\",\"job_id\":\"{job.Id}\"", audit);
        }

        [Fact]
        public async Task A_job_that_throws_is_failed_with_its_message_in_its_record_and_log_only()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-boom", Outcome: "throw", Message: "boom for PATIENT-0042"));
            var failed = await host.WaitFinishedAsync(job.Id);

            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal("boom for PATIENT-0042", failed.Reason);
            Assert.Contains("Job failed: boom for PATIENT-0042", host.LogFile(job.Id));
            Assert.Contains("System.InvalidOperationException", host.LogFile(job.Id));

            // The application log names the job and the exception's type, never its message.
            Assert.Contains(host.Logs.Logs, l => l.Message.Contains(job.Id) && l.Message.Contains("InvalidOperationException"));
            Assert.DoesNotContain(host.Logs.Logs, l => l.Message.Contains("PATIENT-0042"));
        }

        [Fact]
        public async Task A_failed_outcome_ends_the_job_failed_without_an_exception()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(TestJobHandlers.Light, new GatedParameters("auto-no", Outcome: "fail", Message: "nothing to do"));
            var failed = await host.WaitFinishedAsync(job.Id);

            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal("nothing to do", failed.Reason);
            Assert.Contains("Job failed: nothing to do", host.LogFile(job.Id));
        }

        [Fact]
        public async Task The_heavy_lane_runs_one_job_at_a_time_and_the_light_lane_runs_beside_it()
        {
            await using var host = await JobTestHost.StartAsync();

            var first = host.Submit(TestJobHandlers.Heavy, new GatedParameters("h1"));
            var second = host.Submit(TestJobHandlers.Heavy, new GatedParameters("h2"));
            await host.WaitForStateAsync(first.Id, JobState.Running);

            var light = host.Submit(TestJobHandlers.Light, new GatedParameters("l1"));
            await host.WaitForStateAsync(light.Id, JobState.Running);

            // The second heavy job waits while the first runs, however long that is.
            await Task.Delay(300);
            Assert.Equal(JobState.Queued, host.Queue.Find(second.Id).State);
            Assert.False(host.Gates.HasStarted("h2"));
            Assert.Equal(new LaneStatus(JobLane.Heavy, 1, 16, 1, 1), host.Queue.Status(JobLane.Heavy));
            Assert.Equal(new LaneStatus(JobLane.Light, 1, 16, 0, 1), host.Queue.Status(JobLane.Light));

            host.Gates.Release("h1");
            await host.WaitForStateAsync(second.Id, JobState.Running);
            Assert.Equal(JobState.Succeeded, host.Queue.Find(first.Id).State);

            host.Gates.Release("h2");
            host.Gates.Release("l1");
            await host.WaitForStateAsync(second.Id, JobState.Succeeded);
            await host.WaitForStateAsync(light.Id, JobState.Succeeded);
        }

        [Fact]
        public async Task A_full_lane_refuses_with_a_message_and_the_other_lane_still_takes_jobs()
        {
            await using var host = await JobTestHost.StartAsync(configure: o => o.Lanes.QueueCapacity = 2);

            var running = host.Submit(TestJobHandlers.Heavy, new GatedParameters("r"));
            await host.WaitForStateAsync(running.Id, JobState.Running);
            var waiting = host.Submit(TestJobHandlers.Heavy, new GatedParameters("w1"));
            host.Submit(TestJobHandlers.Heavy, new GatedParameters("w2"));

            var refused = host.Queue.Submit(TestJobHandlers.Heavy, new GatedParameters("w3"), "local:tester");
            Assert.False(refused.Accepted);
            Assert.Equal("The heavy lane's queue is full (2 jobs waiting); try again once one has started.", refused.Refusal);
            Assert.Equal(3, host.Queue.List().Count);

            Assert.True(host.Queue.Submit(TestJobHandlers.Light, new GatedParameters("auto-light"), "local:tester").Accepted);

            // A cancelled job no longer waits, so it frees its place.
            Assert.True(host.Queue.Cancel(waiting.Id, "local:tester").Done);
            Assert.True(host.Queue.Submit(TestJobHandlers.Heavy, new GatedParameters("w3"), "local:tester").Accepted);
        }

        [Fact]
        public async Task Cancelling_a_queued_job_ends_it_before_it_ever_starts()
        {
            await using var host = await JobTestHost.StartAsync();

            var blocker = host.Submit(TestJobHandlers.Heavy, new GatedParameters("blocker"));
            await host.WaitForStateAsync(blocker.Id, JobState.Running);
            var queued = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-never"));

            var result = host.Queue.Cancel(queued.Id, "local:tester");

            Assert.True(result.Done);
            var cancelled = host.Queue.Find(queued.Id);
            Assert.Equal(JobState.Cancelled, cancelled.State);
            Assert.Null(cancelled.StartedUtc);
            Assert.Equal("Cancelled by local:tester before it started.", cancelled.Reason);
            Assert.Equal(JobState.Cancelled, OnDisk(host, queued.Id).State);

            host.Gates.Release("blocker");
            await host.WaitForStateAsync(blocker.Id, JobState.Succeeded);
            await Task.Delay(200);
            Assert.False(host.Gates.HasStarted("auto-never"));
            Assert.False(host.Queue.Cancel(queued.Id, "local:tester").Done);
        }

        [Fact]
        public async Task Cancelling_a_running_job_cancels_the_token_its_handler_holds()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("long"));
            await host.WaitForStateAsync(job.Id, JobState.Running);

            var result = host.Queue.Cancel(job.Id, "local:tester");
            Assert.True(result.Done);
            Assert.False(host.Queue.Cancel(job.Id, "local:tester").Done); // once is enough

            var cancelled = await host.WaitFinishedAsync(job.Id);
            Assert.Equal(JobState.Cancelled, cancelled.State);
            Assert.Equal("Cancelled by local:tester.", cancelled.Reason);
            Assert.EndsWith("Job cancelled.\n", host.LogFile(job.Id));
            Assert.Contains($"\"action\":\"job.cancel\",\"job_id\":\"{job.Id}\"", host.AuditFiles());
            Assert.Equal("The job has already finished.", host.Queue.Cancel(job.Id, "local:tester").Message);
        }

        [Fact]
        public async Task Retry_runs_a_failed_job_again_as_the_same_job_with_its_attempt_counted()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-flaky", Outcome: "fail-first"));
            Assert.Equal(JobState.Failed, (await host.WaitFinishedAsync(job.Id)).State);

            var retry = host.Queue.Retry(job.Id, "local:tester");
            Assert.True(retry.Done, retry.Message);
            Assert.Equal("Queued again (attempt 2).", retry.Message);

            var done = await host.WaitForStateAsync(job.Id, JobState.Succeeded);
            Assert.Equal(2, done.Attempt);
            Assert.Null(done.Reason);
            Assert.Equal(2, host.Gates.Runs("auto-flaky"));
            Assert.Single(host.Queue.List());

            // Both runs are in the one log, in order.
            string log = host.LogFile(job.Id);
            Assert.True(log.IndexOf("Job failed:", StringComparison.Ordinal) < log.LastIndexOf("Job succeeded.", StringComparison.Ordinal));
            Assert.Contains($"\"action\":\"job.retry\",\"job_id\":\"{job.Id}\"", host.AuditFiles());

            var again = host.Queue.Retry(job.Id, "local:tester");
            Assert.False(again.Done);
            Assert.Equal("A job that is succeeded cannot be retried.", again.Message);
        }

        [Fact]
        public async Task Unknown_kinds_and_refused_parameters_are_never_queued()
        {
            await using var host = await JobTestHost.StartAsync();

            var unknown = host.Queue.Submit("no-such-kind", new { }, "local:tester");
            var refused = host.Queue.Submit(TestJobHandlers.Heavy, new GatedParameters("refuse"), "local:tester");
            var badRoot = host.Queue.Submit(ScanJobHandler.KindName, new ScanJobParameters("nope", "x"), "local:tester");
            var traversal = host.Queue.Submit(ScanJobHandler.KindName, new ScanJobParameters("archive", "../state"), "local:tester");

            Assert.Equal("There is no job kind 'no-such-kind'.", unknown.Refusal);
            Assert.Equal("Refused by the test handler.", refused.Refusal);
            Assert.Equal("There is no data root with that id.", badRoot.Refusal);
            Assert.Equal("'.' and '..' segments are refused.", traversal.Refusal);
            Assert.Empty(host.Queue.List());
            Assert.Empty(Directory.GetDirectories(Path.Combine(host.Folders.State, "jobs")));
            Assert.Contains("\"action\":\"job.refuse\"", host.AuditFiles());
        }

        [Fact]
        public async Task Without_a_state_root_jobs_are_off_and_say_why()
        {
            await using var host = await JobTestHost.StartAsync(configure: o => o.StateRoot = null);

            Assert.False(host.Queue.IsAvailable);
            var refused = host.Queue.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-x"), "local:tester");

            Assert.False(refused.Accepted);
            Assert.Equal("Jobs need a state folder: set Web:StateRoot in appsettings.json or the environment.", refused.Refusal);
        }

        [Fact]
        public async Task The_event_throttle_calls_back_at_most_four_times_a_second()
        {
            var events = new JobEvents(NullLogger<JobEvents>.Instance);
            int calls = 0;
            using var throttle = new JobEventThrottle(events, () =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });

            for (int i = 0; i < 500; i++)
                events.Publish("0123456789abcdef0123456789abcdef", JobChangeKind.Log);
            await Task.Delay(JobEventThrottle.Interval * 3);

            // One at once, one after the interval for everything that came in between.
            Assert.Equal(2, Volatile.Read(ref calls));
        }

        [Fact]
        public void A_handler_that_throws_in_an_event_does_not_reach_the_job()
        {
            var events = new JobEvents(NullLogger<JobEvents>.Instance);
            int reached = 0;
            events.Changed += _ => throw new InvalidOperationException("subscriber bug");
            events.Changed += _ => reached++;

            events.Publish("0123456789abcdef0123456789abcdef", JobChangeKind.Progress);

            Assert.Equal(1, reached);
        }

        private static JobRecord OnDisk(JobTestHost host, string jobId) =>
            JsonSerializer.Deserialize<JobRecord>(
                File.ReadAllText(Path.Combine(host.Folders.State, "jobs", jobId, JobStore.JobFile)), JobJson.Options);
    }
}
