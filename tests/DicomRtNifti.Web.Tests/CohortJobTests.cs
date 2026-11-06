using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Core.Tests;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The cohort job kinds through the job service with Core and the real native, over
    /// synthesized patients (a 16x16x4 CT and a one-ROI structure set each): a conversion runs end
    /// to end and releases its project; a conversion cancelled mid-run stops at Core's next check,
    /// leaves no lock and says "cancelled" in its log; an anonymized one keeps its key under the
    /// key root; and what Core's grammar refuses, or the job sets itself, is never queued.
    /// </summary>
    public sealed class CohortJobTests
    {
        private const int Seconds = 120;

        [Fact]
        public async Task A_conversion_runs_end_to_end_and_releases_its_project()
        {
            await using var host = await JobTestHost.StartAsync();
            WriteCohort(host.Folders, "PAT_A", "PAT_B");

            var job = host.Submit(CohortConvertJobHandler.KindName, new CohortJobParameters("archive", "cohort", "proj"));

            Assert.Equal("Convert archive:cohort into project proj", job.Title);
            Assert.Equal("DicomRtNifti.Cli --cohort-convert --input archive:cohort --output output:proj", job.Cli);
            Assert.Equal("proj", job.Project);
            Assert.Equal(JobLane.Heavy, job.Lane);

            var done = await host.WaitFinishedAsync(job.Id, Seconds);
            Assert.True(done.State == JobState.Succeeded, done.Reason + "\n" + host.LogFile(job.Id));
            Assert.Equal("2 series converted.", done.Reason);
            Assert.Equal(new JobProgress("series", 2, 2, true, 2, 0, null), done.Progress);

            string project = Path.Combine(host.Folders.Output, "proj");
            Assert.True(File.Exists(Path.Combine(project, "export_manifest.csv")));
            Assert.Equal(2, Directory.GetFiles(project, "image.nii.gz", SearchOption.AllDirectories).Length);
            Assert.False(File.Exists(Path.Combine(project, ProjectLocks.LockFileName)));

            using var result = JsonDocument.Parse(host.Queue.ReadResult(job.Id));
            Assert.Equal("dicomrtnifti.cohort-convert/1", result.RootElement.GetProperty("schema").GetString());
            Assert.Equal("output:proj", result.RootElement.GetProperty("output_root").GetString());
            Assert.Equal(2, result.RootElement.GetProperty("succeeded").GetInt32());
            Assert.Contains("  Planned 2 series (0 skipped, 0 unlinked RT objects)", host.LogFile(job.Id));
        }

        [Fact]
        public async Task A_cancelled_conversion_leaves_no_lock_and_its_log_says_cancelled()
        {
            await using var host = await JobTestHost.StartAsync();
            WriteCohort(host.Folders, "PAT_A", "PAT_B", "PAT_C");

            // Cancel as the first series is recorded, from inside Core's progress callback, so the
            // run is certainly mid-way: Core checks the token before the next series.
            int cancels = 0;
            host.Events.Changed += change =>
            {
                if (change.Kind != JobChangeKind.Progress)
                    return;
                var running = host.Queue.Find(change.JobId);
                if (running?.Kind == CohortConvertJobHandler.KindName && running.Progress.Unit == "series"
                    && running.Progress.Done >= 1 && Interlocked.Exchange(ref cancels, 1) == 0)
                {
                    host.Queue.Cancel(change.JobId, "local:tester");
                }
            };

            var job = host.Submit(CohortConvertJobHandler.KindName, new CohortJobParameters("archive", "cohort", "proj"));
            var cancelled = await host.WaitFinishedAsync(job.Id, Seconds);

            Assert.True(cancelled.State == JobState.Cancelled, cancelled.State + ": " + cancelled.Reason + "\n" + host.LogFile(job.Id));
            Assert.Equal("Cancelled by local:tester.", cancelled.Reason);
            Assert.InRange(cancelled.Progress.Done, 1, 2);
            Assert.Equal(3, cancelled.Progress.Total);

            string project = Path.Combine(host.Folders.Output, "proj");
            Assert.False(File.Exists(Path.Combine(project, ProjectLocks.LockFileName)));
            Assert.Null(host.Services.GetLocks().HolderOf("proj"));
            Assert.False(File.Exists(Path.Combine(project, "export_manifest.csv"))); // stopped before the end

            string log = host.LogFile(job.Id);
            Assert.Contains("cancelled", log, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Job cancelled.\n", log);

            // The project really is free: the next conversion takes it and finishes.
            var next = host.Submit(CohortConvertJobHandler.KindName, new CohortJobParameters("archive", "cohort", "proj"));
            var finished = await host.WaitFinishedAsync(next.Id, Seconds);
            Assert.True(finished.State == JobState.Succeeded, finished.Reason);
            Assert.True(File.Exists(Path.Combine(project, "export_manifest.csv")));
        }

        [Fact]
        public async Task A_survey_writes_only_the_manifest()
        {
            await using var host = await JobTestHost.StartAsync();
            WriteCohort(host.Folders, "PAT_A");

            var job = host.Submit(CohortManifestJobHandler.KindName, new CohortJobParameters("archive", "cohort", "survey"));
            Assert.Equal("DicomRtNifti.Cli --cohort-manifest --input archive:cohort --output output:survey", job.Cli);
            Assert.Equal("Survey archive:cohort into project survey", job.Title);

            var done = await host.WaitFinishedAsync(job.Id, Seconds);
            Assert.True(done.State == JobState.Succeeded, done.Reason);
            Assert.Equal("1 series surveyed.", done.Reason);
            string project = Path.Combine(host.Folders.Output, "survey");
            Assert.True(File.Exists(Path.Combine(project, "export_manifest.csv")));
            Assert.Empty(Directory.GetFiles(project, "*.nii.gz", SearchOption.AllDirectories));
            using var result = JsonDocument.Parse(host.Queue.ReadResult(job.Id));
            Assert.Equal("dicomrtnifti.cohort-manifest/1", result.RootElement.GetProperty("schema").GetString());
        }

        [Fact]
        public async Task An_anonymized_conversion_keeps_its_key_under_the_key_root()
        {
            await using var host = await JobTestHost.StartAsync();
            WriteCohort(host.Folders, "PAT_A");

            var job = host.Submit(CohortConvertJobHandler.KindName,
                new CohortJobParameters("archive", "cohort", "anon", new[] { "--anonymize" }));
            Assert.Contains("--anonymize", job.Cli);
            Assert.Contains("--key-path keys:anon/AnonymizationKey.json", job.Cli);

            var done = await host.WaitFinishedAsync(job.Id, Seconds);
            Assert.True(done.State == JobState.Succeeded, done.Reason);
            Assert.True(File.Exists(Path.Combine(host.Folders.Keys, "anon", "AnonymizationKey.json")));
            Assert.Empty(Directory.GetFiles(host.Folders.Output, "AnonymizationKey.json", SearchOption.AllDirectories));

            string result = host.Queue.ReadResult(job.Id);
            Assert.Contains("\"anonymization_key\": \"keys:anon/AnonymizationKey.json\"", result);
            Assert.DoesNotContain("PAT_A", result);
        }

        [Fact]
        public async Task What_the_job_sets_itself_or_Core_refuses_is_never_queued()
        {
            await using var host = await JobTestHost.StartAsync();
            WriteCohort(host.Folders, "PAT_A");

            string Refusal(CohortJobParameters p) => host.Queue.Submit(CohortConvertJobHandler.KindName, p, "local:tester").Refusal;

            Assert.Equal("--output is set by the job from its folders, not given as an argument.",
                Refusal(new CohortJobParameters("archive", "cohort", "p", new[] { "--output", "/tmp/x" })));
            Assert.Equal("--key-path is set by the job from its folders, not given as an argument.",
                Refusal(new CohortJobParameters("archive", "cohort", "p", new[] { "--anonymize", "--key-path", "k.json" })));
            Assert.Equal("--dry-run is set by the job from its folders, not given as an argument.",
                Refusal(new CohortJobParameters("archive", "cohort", "p", new[] { "--dry-run" })));
            Assert.StartsWith("--output-spacing: ",
                Refusal(new CohortJobParameters("archive", "cohort", "p", new[] { "--output-spacing", "abc" })));
            Assert.Equal("The association set does not exist.",
                Refusal(new CohortJobParameters("archive", "cohort", "p", AssociationsFile: "sets/none.json")));
            Assert.Equal("There is no data root with that id.",
                Refusal(new CohortJobParameters("nope", "cohort", "p")));
            Assert.Empty(host.Queue.List());
        }

        [Fact]
        public async Task An_anonymized_job_without_a_key_root_is_refused()
        {
            await using var host = await JobTestHost.StartAsync(configure: o => o.KeyRoot = null);

            var refused = host.Queue.Submit(CohortConvertJobHandler.KindName,
                new CohortJobParameters("archive", "", "p", new[] { "--anonymize" }), "local:tester");

            Assert.Equal("Anonymized jobs need Web:KeyRoot: the key is never written into the output root.", refused.Refusal);
        }

        [Fact]
        public async Task A_missing_input_folder_fails_the_run_with_its_root_token()
        {
            await using var host = await JobTestHost.StartAsync();

            var job = host.Submit(CohortConvertJobHandler.KindName, new CohortJobParameters("archive", "gone", "p"));
            var failed = await host.WaitFinishedAsync(job.Id, Seconds);

            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal("Input folder not found: archive:gone", failed.Reason);
        }

        internal static void WriteCohort(JobFolders folders, params string[] patients)
        {
            string root = Path.Combine(folders.Data, "cohort");
            foreach (string patient in patients)
                DicomTestData.WritePatientWithStructureSet(root, patient, damagedContours: false);
        }
    }
}
