using System.IO;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// One writer per output project (plan section 3.2): a job holds its project's lock file for
    /// as long as it runs, a second job on the project, here or in another process, is refused
    /// while it does, and the lock is gone once the job ends however it ends. A lock left by a
    /// process that is gone is taken over; one held by a live process is not.
    /// </summary>
    public sealed class ProjectLockTests
    {
        [Fact]
        public async Task Two_jobs_never_write_one_project_at_once()
        {
            // Two heavy workers, so the second job on the project would run beside the first.
            await using var host = await JobTestHost.StartAsync(configure: o => o.Lanes.MaxHeavyWorkers = 2);

            var first = host.Submit(TestJobHandlers.Heavy, new GatedParameters("first", Project: "shared"));
            await host.WaitForStateAsync(first.Id, JobState.Running);
            Assert.True(File.Exists(LockFile(host, "shared")));
            Assert.Equal(first.Id, host.Services.GetLocks().HolderOf("shared"));

            var second = host.Submit(TestJobHandlers.Heavy, new GatedParameters("second", Project: "shared"));
            var refused = await host.WaitFinishedAsync(second.Id);
            Assert.Equal(JobState.Failed, refused.State);
            Assert.Equal($"Project 'shared' is in use by job {first.Id}.", refused.Reason);
            Assert.False(host.Gates.HasStarted("second"));

            // Another project is not held up.
            var other = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-other", Project: "elsewhere"));
            await host.WaitForStateAsync(other.Id, JobState.Succeeded);

            host.Gates.Release("first");
            await host.WaitForStateAsync(first.Id, JobState.Succeeded);
            Assert.False(File.Exists(LockFile(host, "shared")));
            Assert.Null(host.Services.GetLocks().HolderOf("shared"));

            // Free again: the refused job runs on retry.
            Assert.True(host.Queue.Retry(second.Id, "local:tester").Done);
            host.Gates.Release("second");
            await host.WaitForStateAsync(second.Id, JobState.Succeeded);
        }

        [Fact]
        public async Task A_failed_or_cancelled_job_leaves_no_lock()
        {
            await using var host = await JobTestHost.StartAsync();

            var failing = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-fails", Project: "p", Outcome: "throw"));
            await host.WaitForStateAsync(failing.Id, JobState.Failed);
            Assert.False(File.Exists(LockFile(host, "p")));

            var cancelled = host.Submit(TestJobHandlers.Heavy, new GatedParameters("held", Project: "p"));
            await host.WaitForStateAsync(cancelled.Id, JobState.Running);
            Assert.True(File.Exists(LockFile(host, "p")));
            host.Queue.Cancel(cancelled.Id, "local:tester");
            await host.WaitForStateAsync(cancelled.Id, JobState.Cancelled);
            Assert.False(File.Exists(LockFile(host, "p")));
            Assert.Null(host.Services.GetLocks().HolderOf("p"));
        }

        [Fact]
        public async Task A_lock_left_by_a_process_that_is_gone_is_taken_over()
        {
            await using var host = await JobTestHost.StartAsync();
            JobRecoveryTests.WriteStamp(LockFile(host, "p"), OtherLiveProcess.ExitedProcessId(), new string('b', 32));

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-runs", Project: "p"));

            Assert.Equal(JobState.Succeeded, (await host.WaitFinishedAsync(job.Id)).State);
            Assert.False(File.Exists(LockFile(host, "p")));
        }

        [Fact]
        public async Task A_lock_held_by_a_live_process_refuses_the_job_and_stays()
        {
            await using var host = await JobTestHost.StartAsync();
            using var other = new OtherLiveProcess();
            string holder = new string('c', 32);
            JobRecoveryTests.WriteStamp(LockFile(host, "p"), other.Id, holder);

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-blocked", Project: "p"));
            var failed = await host.WaitFinishedAsync(job.Id);

            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal($"Project 'p' is in use by process {other.Id} on {System.Environment.MachineName} (job {holder}).", failed.Reason);
            Assert.True(File.Exists(LockFile(host, "p")));
        }

        [Fact]
        public async Task An_unreadable_lock_file_refuses_and_names_the_file_by_its_root_token()
        {
            await using var host = await JobTestHost.StartAsync();
            Directory.CreateDirectory(Path.Combine(host.Folders.Output, "p"));
            File.WriteAllText(LockFile(host, "p"), "");

            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-blocked", Project: "p"));
            var failed = await host.WaitFinishedAsync(job.Id);

            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal(
                "Project 'p' has a lock file that cannot be read (output:p/.dicomrtnifti-job.lock). Delete it if no other process is writing this project.",
                failed.Reason);
        }

        [Theory]
        [InlineData("", "A project is one folder name under the output root.")]
        [InlineData("a/b", "A project is one folder name under the output root.")]
        [InlineData("..", "The project name is refused: '.' and '..' segments are refused.")]
        public async Task A_project_must_be_one_folder_under_the_output_root(string project, string refusal)
        {
            await using var host = await JobTestHost.StartAsync();

            var result = host.Queue.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-x", Project: project), "local:tester");

            Assert.False(result.Accepted);
            Assert.Equal(refusal, result.Refusal);
        }

        private static string LockFile(JobTestHost host, string project) =>
            Path.Combine(host.Folders.Output, project, ProjectLocks.LockFileName);
    }

    internal static class JobServiceProviderExtensions
    {
        public static ProjectLocks GetLocks(this System.IServiceProvider services) =>
            (ProjectLocks)services.GetService(typeof(ProjectLocks));
    }
}
