using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// Restart recovery (plan section 3.2): a server killed mid-job restarts with that job
    /// interrupted, its reason recorded and its project lock released, and the jobs that were
    /// waiting queued again; Retry runs the interrupted one. A graceful stop interrupts the same
    /// way. One server owns a state folder: a live owner keeps a second one's jobs off.
    /// </summary>
    public sealed class JobRecoveryTests
    {
        [Fact]
        public async Task A_killed_server_restarts_with_its_running_job_interrupted_and_retryable()
        {
            using var folders = JobFolders.Create();
            await using var first = await JobTestHost.StartAsync(folders);
            var running = first.Submit(TestJobHandlers.Heavy, new GatedParameters("long", Project: "p1"));
            await first.WaitForStateAsync(running.Id, JobState.Running);
            var waiting = first.Submit(TestJobHandlers.Heavy, new GatedParameters("next"));
            Assert.True(File.Exists(LockFile(folders, "p1")));

            // The kill: the files exactly as the first server has them now, job file, log, owner
            // lock and project lock included, under a second server that never saw them written.
            using var killed = folders.SnapshotAsKilled();
            Assert.Equal(JobState.Running, OnDisk(killed, running.Id).State);
            Assert.True(File.Exists(LockFile(killed, "p1")));

            await using var restarted = await JobTestHost.StartAsync(killed);

            Assert.True(restarted.Queue.IsAvailable, restarted.Queue.UnavailableReason);
            var interrupted = restarted.Queue.Find(running.Id);
            Assert.Equal(JobState.Interrupted, interrupted.State);
            Assert.Equal("The server stopped while this job was running (found at the next start). Retry runs it again.", interrupted.Reason);
            Assert.NotNull(interrupted.FinishedUtc);
            Assert.Equal(JobState.Interrupted, OnDisk(killed, running.Id).State);
            Assert.False(File.Exists(LockFile(killed, "p1")));
            Assert.Contains($"\"user\":\"server\",\"action\":\"job.interrupt\",\"job_id\":\"{running.Id}\"", restarted.AuditFiles());
            Assert.Contains(restarted.Logs.Logs, l => l.Message.Contains("1 interrupted by the last stop, 1 queued again"));

            // What was waiting runs.
            await restarted.WaitForStateAsync(waiting.Id, JobState.Running);
            restarted.Gates.Release("next");
            await restarted.WaitForStateAsync(waiting.Id, JobState.Succeeded);

            // Retry runs the interrupted job again, under the project lock it lost.
            Assert.True(restarted.Queue.Retry(running.Id, "local:tester").Done);
            var again = await restarted.WaitForStateAsync(running.Id, JobState.Running);
            Assert.Equal(2, again.Attempt);
            Assert.True(File.Exists(LockFile(killed, "p1")));
            restarted.Gates.Release("long");
            await restarted.WaitForStateAsync(running.Id, JobState.Succeeded);
            Assert.False(File.Exists(LockFile(killed, "p1")));

            // The log holds the first server's lines and the retry's.
            string log = restarted.LogFile(running.Id);
            Assert.Contains("(attempt 1)", log);
            Assert.Contains("(attempt 2)", log);
        }

        [Fact]
        public async Task A_graceful_stop_interrupts_the_running_job_and_keeps_the_queue()
        {
            using var folders = JobFolders.Create();
            string runningId, waitingId;
            await using (var first = await JobTestHost.StartAsync(folders))
            {
                runningId = first.Submit(TestJobHandlers.Heavy, new GatedParameters("long", Project: "p1")).Id;
                await first.WaitForStateAsync(runningId, JobState.Running);
                waitingId = first.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-next")).Id;

                await first.StopAsync();

                var stopped = first.Queue.Find(runningId);
                Assert.Equal(JobState.Interrupted, stopped.State);
                Assert.Equal("The server stopped while this job was running. Retry runs it again.", stopped.Reason);
                Assert.EndsWith("Job interrupted: the server is stopping.\n", first.LogFile(runningId));
                Assert.Equal(JobState.Queued, OnDisk(folders, waitingId).State);
                Assert.False(File.Exists(LockFile(folders, "p1")));
                Assert.False(File.Exists(Path.Combine(folders.State, "jobs", "owner.lock")));
            }

            await using var second = await JobTestHost.StartAsync(folders);
            Assert.Equal(JobState.Interrupted, second.Queue.Find(runningId).State);
            await second.WaitForStateAsync(waitingId, JobState.Succeeded);
        }

        [Fact]
        public async Task A_job_file_that_cannot_be_read_is_skipped_and_left_alone()
        {
            using var folders = JobFolders.Create();
            string broken = new string('a', 32);
            string brokenFile = Path.Combine(folders.State, "jobs", broken, JobStore.JobFile);
            Directory.CreateDirectory(Path.GetDirectoryName(brokenFile));
            File.WriteAllText(brokenFile, "{ \"schema\": \"dicomrtnifti.web-job/1\", \"id\": ");

            await using var host = await JobTestHost.StartAsync(folders);

            Assert.True(host.Queue.IsAvailable);
            Assert.Null(host.Queue.Find(broken));
            Assert.Contains(host.Logs.Logs, l => l.Message == $"Skipped job {broken}: its job file cannot be read (JsonException).");
            Assert.True(File.Exists(brokenFile));
            var job = host.Submit(TestJobHandlers.Light, new GatedParameters("auto-fine"));
            await host.WaitForStateAsync(job.Id, JobState.Succeeded);
        }

        [Fact]
        public async Task An_owner_lock_left_by_a_dead_process_is_taken_over()
        {
            using var folders = JobFolders.Create();
            WriteStamp(Path.Combine(folders.State, "jobs", "owner.lock"), OtherLiveProcess.ExitedProcessId(), null);

            await using var host = await JobTestHost.StartAsync(folders);

            Assert.True(host.Queue.IsAvailable, host.Queue.UnavailableReason);
        }

        [Fact]
        public async Task A_live_owner_keeps_a_second_servers_jobs_off()
        {
            using var folders = JobFolders.Create();
            using var other = new OtherLiveProcess();
            WriteStamp(Path.Combine(folders.State, "jobs", "owner.lock"), other.Id, null);

            await using var host = await JobTestHost.StartAsync(folders);

            Assert.False(host.Queue.IsAvailable);
            Assert.Equal(
                $"Another server process (id {other.Id} on {Environment.MachineName}) owns this state folder; jobs are off in this one.",
                host.Queue.UnavailableReason);
            Assert.False(host.Queue.Submit(TestJobHandlers.Light, new GatedParameters("auto-x"), "local:tester").Accepted);
        }

        internal static void WriteStamp(string path, int processId, string jobId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(
                new JobStore.ProcessStamp(processId, Environment.MachineName, jobId, DateTimeOffset.UtcNow), JobJson.Compact));
        }

        private static string LockFile(JobFolders folders, string project) =>
            Path.Combine(folders.Output, project, ProjectLocks.LockFileName);

        private static JobRecord OnDisk(JobFolders folders, string jobId) =>
            JsonSerializer.Deserialize<JobRecord>(
                JobTestHost.ReadShared(Path.Combine(folders.State, "jobs", jobId, JobStore.JobFile)), JobJson.Options);
    }
}
