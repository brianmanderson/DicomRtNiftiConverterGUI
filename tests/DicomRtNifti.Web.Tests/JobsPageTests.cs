using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using DicomRtNifti.Web.Audit;
using DicomRtNifti.Web.Features.Audit;
using DicomRtNifti.Web.Features.Jobs;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The Jobs and Audit pages (plan sections 4.8 and 4.15), rendered with bUnit over a running
    /// job service: the running job's progress and log tail, redacted, kept current by the
    /// throttled events; Cancel and Retry; the queue and the history; and an audit log that only
    /// an administrator sees.
    /// </summary>
    public sealed class JobsPageTests
    {
        [Fact]
        public async Task The_page_follows_a_running_job_and_cancel_stops_it()
        {
            await using var host = await JobTestHost.StartAsync();
            using var context = Harness(host);
            string secret = Path.Combine(host.Folders.Data, "cohort", "case-1");
            var job = host.Submit(TestJobHandlers.Heavy, new GatedParameters("live", Message: "reading " + secret));
            await host.WaitForStateAsync(job.Id, JobState.Running);

            var page = context.Render<JobsPage>();

            Assert.Equal("Jobs", page.Find("h1").TextContent);
            var card = page.Find(".job-running");
            Assert.Contains("Test live", card.TextContent);
            Assert.Contains("0 of 2 steps done", page.Find(".job-progress").TextContent);
            Assert.Equal("2", page.Find(".job-running progress").GetAttribute("max"));
            // The log tail is redacted: the server path shows as its root token.
            string log = page.Find(".job-log").TextContent;
            Assert.Contains("reading archive:cohort", log);
            Assert.DoesNotContain(host.Folders.Root, page.Markup, StringComparison.OrdinalIgnoreCase);

            page.Find(".job-running .job-cancel").Click();

            await host.WaitForStateAsync(job.Id, JobState.Cancelled);
            page.WaitForAssertion(() =>
            {
                Assert.Empty(page.FindAll(".job-running"));
                Assert.Contains("Cancelled", page.Find(".jobs-history").TextContent);
                Assert.Contains("Job cancelled.", page.Find(".job-log").TextContent);
            }, TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task The_page_shows_the_queue_and_cancels_a_waiting_job()
        {
            await using var host = await JobTestHost.StartAsync();
            using var context = Harness(host);
            var blocker = host.Submit(TestJobHandlers.Heavy, new GatedParameters("blocker"));
            await host.WaitForStateAsync(blocker.Id, JobState.Running);
            host.Submit(TestJobHandlers.Heavy, new GatedParameters("waiting"));

            var page = context.Render<JobsPage>();

            var queued = page.Find(".jobs-queued");
            Assert.Contains("Test waiting", queued.TextContent);
            Assert.Contains("1 of 16", page.Find(".jobs-lanes").TextContent);

            queued.QuerySelector(".job-cancel").Click();

            Assert.Equal("Cancelled before it started.", page.Find(".jobs-message").TextContent);
            Assert.Empty(page.FindAll(".jobs-queued"));
            Assert.Contains("Test waiting", page.Find(".jobs-history").TextContent);
            host.Gates.Release("blocker");
        }

        [Fact]
        public async Task Retry_on_the_page_queues_an_interrupted_job_again()
        {
            using var folders = JobFolders.Create();
            string jobId;
            await using (var first = await JobTestHost.StartAsync(folders))
            {
                jobId = first.Submit(TestJobHandlers.Heavy, new GatedParameters("auto-later")).Id;
                await first.WaitForStateAsync(jobId, JobState.Succeeded);
            }

            // Make it an interrupted job, as a restart would find it.
            string file = Path.Combine(folders.State, "jobs", jobId, JobStore.JobFile);
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"state\": \"succeeded\"", "\"state\": \"running\""));
            await using var host = await JobTestHost.StartAsync(folders);
            using var context = Harness(host);

            var page = context.Render<JobsPage>();
            Assert.Contains("Interrupted", page.Find(".jobs-history").TextContent);
            Assert.Contains("The server stopped while this job was running", page.Find(".job-reason").TextContent);

            page.Find(".jobs-history .job-retry").Click();

            Assert.Equal("Queued again (attempt 2).", page.Find(".jobs-message").TextContent);
            var done = await host.WaitForStateAsync(jobId, JobState.Succeeded);
            Assert.Equal(2, done.Attempt);
            page.WaitForAssertion(() => Assert.Contains("Succeeded", page.Find(".job-state").TextContent), TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task Without_a_state_root_the_page_says_jobs_are_off()
        {
            await using var host = await JobTestHost.StartAsync(configure: o => o.StateRoot = null);
            using var context = Harness(host);

            var page = context.Render<JobsPage>();

            Assert.Equal(
                "Jobs need a state folder: set Web:StateRoot in appsettings.json or the environment.",
                page.Find(".jobs-off").TextContent);
            Assert.Contains("Nothing is running.", page.Markup);
        }

        [Fact]
        public async Task The_audit_page_lists_what_happened_newest_first_and_filters_by_action()
        {
            await using var host = await JobTestHost.StartAsync();
            using var context = Harness(host);
            var job = host.Submit(TestJobHandlers.Light, new GatedParameters("auto-audited"));
            await host.WaitForStateAsync(job.Id, JobState.Succeeded);

            var page = context.Render<AuditPage>();

            Assert.Equal("Audit", page.Find("h1").TextContent);
            var actions = page.FindAll(".audit-entries tbody tr td:nth-child(3)").Select(td => td.TextContent).ToList();
            Assert.Equal(new[] { "job.finish", "job.start", "job.submit" }, actions);
            Assert.Contains("local:tester", page.Find(".audit-entries").TextContent);

            page.Find("#audit-action").Change(AuditActions.JobSubmitted);

            Assert.Equal(new[] { "job.submit" }, page.FindAll(".audit-entries tbody tr td:nth-child(3)").Select(td => td.TextContent));
        }

        [Fact]
        public async Task The_audit_page_is_for_administrators_only()
        {
            await using var host = await JobTestHost.StartAsync();
            using var context = Harness(host, new Viewer());
            host.Submit(TestJobHandlers.Light, new GatedParameters("auto-x"));

            var page = context.Render<AuditPage>();

            Assert.Equal("The audit log is for administrators.", page.Find(".audit-denied").TextContent);
            Assert.Empty(page.FindAll(".audit-entries"));
        }

        /// <summary>A bUnit context whose pages see the services of <paramref name="host"/>.</summary>
        private static BunitContext Harness(JobTestHost host, ICurrentUser user = null)
        {
            var context = ComponentHarness.Create(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebOptions>>().Value);
            context.Services.AddSingleton(host.Queue);
            context.Services.AddSingleton(host.Events);
            context.Services.AddSingleton(host.Services.GetRequiredService<AuditLog>());
            context.Services.AddSingleton<ICurrentUser>(user ?? new LocalUser());
            return context;
        }

        private sealed class Viewer : ICurrentUser
        {
            public string Id => "local:viewer";

            public bool IsAdministrator => false;
        }
    }
}
