using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bunit;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Web.Audit;
using DicomRtNifti.Web.Features.Curation;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Features.Plan;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The three pages of the forward path rendered with bUnit over a running job service:
    /// ROI curation scans for names and shows Core's clusters, uncovered names and coverage,
    /// merges a cluster into an association, saves the set and offers it for download as the
    /// saved bytes; an import that does not parse replaces nothing; Export setup shows Core's
    /// message for a bad spacing and the live command for a good one; Plan review builds the plan
    /// and shows Core's document, and Run conversion queues the conversion.
    /// </summary>
    public sealed class ExportPagesTests
    {
        private const int Seconds = 120;

        [Fact]
        public async Task Curation_scans_for_names_shows_cores_clusters_and_saves_a_merged_set()
        {
            await using var host = await ExportFlow.StartAsync();
            ExportFlow.WriteCohort(host.Folders);
            using var context = Harness(host);
            var session = context.Services.GetRequiredService<ExportSession>();
            session.InputRootId = "archive";
            session.InputPath = "cohort";
            session.Project = "proj";

            var page = context.Render<CurationPage>();
            Assert.Equal("ROI curation", page.Find("h1").TextContent);
            page.Find(".curation-run").Click();
            var job = await JobWait.FinishedAsync(host.Queue, session.AnalysisJobId, Seconds);
            Assert.True(job.State == JobState.Succeeded, job.Reason + "\n" + host.LogFile(job.Id));

            page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".curation-names tbody tr")), TimeSpan.FromSeconds(10));
            Assert.Contains("--cohort-analyze --input archive:cohort", page.Find(".curation-cli").TextContent);
            Assert.Equal(new[] { "GTV", "spinal cord", "Spinal_Cord" }, page.FindAll(".curation-names tbody tr td:first-child").Select(c => c.TextContent));
            var cluster = page.Find(".curation-clusters li");
            Assert.Contains("spinal cord, Spinal_Cord normalize equal (2 series)", cluster.TextContent);
            Assert.Contains("2 of 2", page.Find(".curation-coverage").TextContent);

            cluster.QuerySelector(".curation-merge").Click();
            Assert.Equal("spinal cord", page.Find(".curation-canonical").GetAttribute("value"));
            Assert.Equal("Spinal_Cord", page.Find(".curation-aliases").GetAttribute("value"));
            // Recounted by Core against the edited set: one canonical name now covers both patients.
            Assert.Contains("spinal cord", page.Find(".curation-coverage").TextContent);
            Assert.DoesNotContain("Spinal_Cord", page.Find(".curation-coverage").TextContent);
            Assert.NotNull(page.Find(".curation-unsaved"));

            page.Find(".curation-set-name").Change("hn");
            page.Find(".curation-save").Click();
            Assert.Contains("Saved 1 association(s) as 'hn' in project proj.", page.Find(".curation-message").TextContent);
            Assert.Equal("hn", session.AssociationSet);
            Assert.Empty(page.FindAll(".curation-unsaved"));

            string saved = File.ReadAllText(Path.Combine(host.Folders.State, "projects", "proj", "associations", "hn", "roi_associations.json"));
            string href = page.Find(".curation-download").GetAttribute("href");
            Assert.StartsWith("data:application/json;charset=utf-8;base64,", href);
            Assert.Equal(saved, Encoding.UTF8.GetString(Convert.FromBase64String(href.Substring(href.IndexOf(',') + 1))));
            Assert.Equal("roi_associations.json", page.Find(".curation-download").GetAttribute("download"));

            // The ROI selection is the session's, which the export takes as --rois.
            page.FindAll(".curation-select")[0].Change(true);
            Assert.Equal(new[] { "GTV" }, session.Options.RoiSelection);
        }

        [Fact]
        public async Task An_import_that_does_not_parse_replaces_nothing()
        {
            await using var host = await ExportFlow.StartAsync();
            using var context = Harness(host);
            var session = context.Services.GetRequiredService<ExportSession>();
            session.Project = "proj";
            host.Services.GetRequiredService<ProjectStore>().SaveAssociations("proj", "hn", new[]
            {
                new RoiAssociation { CanonicalName = "GTV", Aliases = new List<string> { "gtv1" } },
            });
            session.AssociationSet = "hn";

            var page = context.Render<CurationPage>();
            Assert.Equal("GTV", page.Find(".curation-canonical").GetAttribute("value"));

            page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
                .UploadFiles(InputFileContent.CreateFromText("{ not an association set", "bad.json"));

            page.WaitForAssertion(() => Assert.Contains("Nothing was replaced.", page.Find(".curation-error").TextContent));
            Assert.Equal("GTV", page.Find(".curation-canonical").GetAttribute("value"));

            page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
                .UploadFiles(InputFileContent.CreateFromText("[{\"CanonicalName\":\"Brainstem\",\"Aliases\":[\"BS\"]}]", "good.json"));
            page.WaitForAssertion(() => Assert.Equal("Brainstem", page.Find(".curation-canonical").GetAttribute("value")));
            Assert.NotNull(page.Find(".curation-unsaved"));
        }

        [Fact]
        public async Task Export_setup_shows_cores_message_for_a_bad_spacing_and_the_live_command_for_a_good_one()
        {
            await using var host = await ExportFlow.StartAsync();
            Directory.CreateDirectory(Path.Combine(host.Folders.Data, "cohort"));
            using var context = Harness(host);
            var session = context.Services.GetRequiredService<ExportSession>();
            session.InputRootId = "archive";
            session.InputPath = "cohort";
            session.Project = "proj";

            var page = context.Render<ExportPage>();
            Assert.Equal("Export setup", page.Find("h1").TextContent);
            Assert.Equal("DicomRtNifti.Cli --cohort-convert --input archive:cohort --output output:proj", page.Find(".export-cli-convert").TextContent);
            Assert.Equal("DicomRtNifti.Cli --cohort-manifest --input archive:cohort --output output:proj", page.Find(".export-cli-survey").TextContent);

            page.Find(".opt-spacing").Change("1,0,1");
            Assert.Equal("Output spacing: spacing component '0' must be greater than zero", page.Find(".opt-spacing-error").TextContent);

            page.Find(".opt-spacing").Change("1.5,1.5,3");
            Assert.Empty(page.FindAll(".opt-spacing-error"));
            page.Find(".opt-images").Change(false);
            page.Find(".opt-image-tags").Change("Modality,@VoxelSize");
            Assert.Equal(
                "DicomRtNifti.Cli --cohort-convert --input archive:cohort --output output:proj --output-spacing 1.5,1.5,3 --no-images --metadata-tags Modality,@VoxelSize",
                page.Find(".export-cli-convert").TextContent);
            // The survey has no flag for the conversion's toggles, so it does not carry them.
            Assert.Equal("DicomRtNifti.Cli --cohort-manifest --input archive:cohort --output output:proj --output-spacing 1.5,1.5,3",
                page.Find(".export-cli-survey").TextContent);

            page.Find(".opt-structure-tags").Change("NotAKeyword");
            Assert.StartsWith("Structure keywords: ", page.Find(".opt-structure-tags-error").TextContent);

            // Anonymized, the command names the project's salt by a token; a new project's is pending.
            page.Find(".opt-anonymize").Change(true);
            Assert.Contains("--anonymize --salt $PROJECT_SALT --key-path keys:proj/AnonymizationKey.json", page.Find(".export-cli-convert").TextContent);
            Assert.Contains("drawn at random when its first anonymized job is submitted", page.Find(".export-salt").TextContent);
            Assert.False(File.Exists(Path.Combine(host.Folders.Keys, "proj", "AnonymizationKey.json")));

            // A preset keeps the toggles, for this user only.
            page.Find(".preset-name").Change("mine");
            page.Find(".preset-save").Click();
            Assert.Equal("Saved preset 'mine'.", page.Find(".export-message").TextContent);
            Assert.True(host.Services.GetRequiredService<UserPreferences>().Load(new LocalUser().Id, "mine").AnonymizeExport);
        }

        [Fact]
        public async Task Plan_review_shows_cores_plan_and_run_conversion_queues_the_export()
        {
            await using var host = await ExportFlow.StartAsync();
            ExportFlow.WriteCohort(host.Folders);
            using var context = Harness(host);
            var session = context.Services.GetRequiredService<ExportSession>();
            session.InputRootId = "archive";
            session.InputPath = "cohort";
            session.Project = "proj";

            var page = context.Render<PlanPage>();
            Assert.Equal("Plan review", page.Find("h1").TextContent);
            Assert.Equal("DicomRtNifti.Cli --cohort-convert --input archive:cohort --output output:proj --dry-run", page.Find(".plan-cli").TextContent);

            page.Find(".plan-build").Click();
            var plan = await JobWait.FinishedAsync(host.Queue, session.PlanJobId, Seconds);
            Assert.True(plan.State == JobState.Succeeded, plan.Reason + "\n" + host.LogFile(plan.Id));
            page.WaitForAssertion(() => Assert.Equal("2", page.Find(".plan-count").TextContent), TimeSpan.FromSeconds(10));
            Assert.Equal(2, page.FindAll(".plan-series tbody tr").Count);
            Assert.Contains("MRN-H-001", page.Find(".plan-tree").TextContent);
            Assert.Empty(page.FindAll(".plan-stale"));

            // Changing an option marks the plan stale.
            session.PlanMode = CohortMode.Manifest;
            page.Find(".plan-survey").Change(true);
            Assert.NotNull(page.Find(".plan-stale"));

            page.Find(".plan-run-convert").Click();
            Assert.Contains("Queued: Convert archive:cohort into project proj.", page.Find(".plan-message").TextContent);
            var convert = host.Queue.List().Single(j => j.Kind == CohortConvertJobHandler.KindName);
            var done = await JobWait.FinishedAsync(host.Queue, convert.Id, Seconds);
            Assert.True(done.State == JobState.Succeeded, done.Reason + "\n" + host.LogFile(done.Id));
            Assert.True(File.Exists(Path.Combine(host.Folders.Output, "proj", "export_manifest.csv")));
        }

        private static BunitContext Harness(JobTestHost host)
        {
            var context = ComponentHarness.Create(host.Services.GetRequiredService<IOptions<WebOptions>>().Value);
            context.Services.AddSingleton(host.Queue);
            context.Services.AddSingleton(host.Events);
            context.Services.AddSingleton<ICurrentUser>(new LocalUser());
            foreach (var type in new[] { typeof(ProjectStore), typeof(UserPreferences), typeof(ExportCommandBuilder), typeof(RoiScanCache), typeof(ICohortScanSource), typeof(RootPathPresenter) })
                context.Services.AddSingleton(type, host.Services.GetRequiredService(type));
            // One circuit: its session, and no Inventory selection.
            context.Services.AddSingleton(new ExportSession());
            context.Services.AddSingleton<IInventorySelection>(new NoInventorySelection());
            return context;
        }
    }
}
