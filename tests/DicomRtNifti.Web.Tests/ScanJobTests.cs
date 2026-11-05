using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// A scan job end to end in the web app as Program builds it, over synthesized patients: queued
    /// through the job service, run on the light lane, its file counts as progress, its result the
    /// document Core builds for the folder, byte for byte, the input root as its root token; and
    /// the Jobs page shows it, with no server path in the page.
    /// </summary>
    public sealed class ScanJobTests
    {
        [Fact]
        public async Task A_scan_job_runs_end_to_end_in_the_web_app()
        {
            using var folders = JobFolders.Create();
            CohortJobTests.WriteCohort(folders, "PAT_A", "PAT_B");
            string cohort = Path.Combine(folders.Data, "cohort");
            int files = Directory.GetFiles(cohort, "*", SearchOption.AllDirectories).Length;

            using var factory = new WebAppFactory(JobWait.Settings(folders));
            var queue = factory.Services.GetRequiredService<JobQueue>();
            Assert.True(queue.IsAvailable, queue.UnavailableReason);

            var submission = queue.Submit(ScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"), "local:tester");
            Assert.True(submission.Accepted, submission.Refusal);
            Assert.Equal("Scan archive:cohort", submission.Job.Title);
            Assert.Equal("DicomRtNifti.Cli --cohort-scan --input archive:cohort", submission.Job.Cli);
            Assert.Equal(JobLane.Light, submission.Job.Lane);

            var done = await JobWait.FinishedAsync(queue, submission.Job.Id);
            Assert.True(done.State == JobState.Succeeded, done.Reason);
            Assert.Equal($"{files} file(s): 2 patient(s), 2 image series.", done.Reason);
            Assert.Equal(new JobProgress("files", files, files, true), done.Progress);

            // The result is Core's document for the folder, with the input root as its token.
            string result = queue.ReadResult(done.Id);
            var scan = await new DicomScannerService().ScanFolderAsync(cohort, null, CancellationToken.None);
            string expected = CohortDocuments.Serialize(CohortScanDocument.Build(scan, cohort, _ => "archive:cohort"));
            Assert.Equal(expected, result);
            using (var document = JsonDocument.Parse(result))
            {
                Assert.Equal("dicomrtnifti.cohort-scan/1", document.RootElement.GetProperty("schema").GetString());
                Assert.Equal("archive:cohort", document.RootElement.GetProperty("input_root").GetString());
                Assert.Equal(new[] { "PAT_A", "PAT_B" },
                    document.RootElement.GetProperty("patients").EnumerateArray().Select(p => p.GetProperty("patient_id").GetString()));
            }

            // The Jobs page lists it, and shows no server path anywhere.
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/jobs");
            string html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Scan archive:cohort", html);
            Assert.Contains("Succeeded", html);
            Assert.Contains("--cohort-scan --input archive:cohort", html);
            Assert.DoesNotContain(folders.Root, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(folders.Root.Replace('\\', '/'), html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
