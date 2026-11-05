using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Web.Audit;
using DicomRtNifti.Web.Features.DataSources;
using DicomRtNifti.Web.Features.Inventory;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The Data sources and Inventory pages (plan sections 4.2 and 4.3), rendered with bUnit over a
    /// running job service: browsing the roots, scanning, following the job, opening the snapshot;
    /// then the tree with its badges (each the <see cref="SeriesFlags"/> predicate's), expanding,
    /// filtering, the three inspectors and the tags tab, the selection and its CLI equivalent, and
    /// virtualization of a large tree.
    /// </summary>
    public sealed class InventoryPageTests
    {
        [Fact]
        public async Task Data_sources_browses_scans_and_opens_the_inventory()
        {
            await using var host = await StartAsync();
            InventoryCohort.Write(Path.Combine(host.Folders.Data, "cohort"));
            using var context = Harness(host);

            var page = context.Render<DataSourcesPage>();

            Assert.Equal("Data sources", page.Find("h1").TextContent);
            Assert.Contains("No scan yet.", page.Find(".ds-no-scans").TextContent);
            Assert.Equal("archive:", page.Find(".ds-crumbs").TextContent.Trim());
            Assert.Contains("--cohort-scan --input archive: --json-out scan.json", page.Find(".cli pre").TextContent);

            page.FindAll(".ds-folder").Single(b => b.TextContent == "cohort").Click();

            Assert.Equal(new[] { "PAT_A", "PAT_B" }, page.FindAll(".ds-folder").Select(b => b.TextContent));
            Assert.Contains("--cohort-scan --input archive:cohort --json-out scan.json", page.Find(".cli pre").TextContent);

            page.Find(".ds-scan").Click();
            Assert.Equal("Queued: Scan archive:cohort.", page.Find(".ds-message").TextContent);

            page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".ds-inventory")), TimeSpan.FromSeconds(30));
            Assert.Contains("Succeeded", page.Find(".ds-scan-card").TextContent);
            Assert.DoesNotContain(host.Folders.Root, page.Markup, StringComparison.OrdinalIgnoreCase);

            page.Find(".ds-inventory").Click();

            var session = context.Services.GetRequiredService<ScanSession>();
            var job = host.Queue.List().Single(j => j.Kind == SnapshotScanJobHandler.KindName);
            Assert.Equal(job.Id, session.SnapshotId);
            Assert.EndsWith("/inventory", context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
        }

        [Fact]
        public async Task Data_sources_refuses_a_folder_that_is_not_there()
        {
            await using var host = await StartAsync();
            using var context = Harness(host);
            var page = context.Render<DataSourcesPage>();

            // The folder vanishes between listing and opening.
            string gone = Path.Combine(host.Folders.Data, "gone");
            Directory.CreateDirectory(gone);
            page.Find(".ds-crumb").Click();
            var folder = page.FindAll(".ds-folder").Single(b => b.TextContent == "gone");
            Directory.Delete(gone);
            folder.Click();

            Assert.Equal("archive:gone does not exist or is not available.", page.Find(".ds-browse-error").TextContent);
            Assert.True(page.Find(".ds-scan").HasAttribute("disabled"));
        }

        [Fact]
        public async Task The_inventory_says_when_there_is_no_scan()
        {
            await using var host = await StartAsync();
            using var context = Harness(host);

            var page = context.Render<InventoryPage>();

            Assert.Contains("No scan yet.", page.Find(".inv-no-scan").TextContent);
        }

        [Fact]
        public async Task The_tree_badges_expands_inspects_and_filters()
        {
            await using var host = await StartAsync();
            var cohort = InventoryCohort.Write(Path.Combine(host.Folders.Data, "cohort"));
            using var context = Harness(host);
            await OpenSnapshotAsync(host, context);

            var page = context.Render<InventoryPage>();

            Assert.Contains("archive:cohort", page.Find(".inv-source").TextContent);
            Assert.Equal(2, page.FindAll(".inv-row").Count);
            Assert.Equal("Patient inspector", page.Find(".inv-inspector-head").TextContent);
            var patientA = page.FindAll(".inv-row")[0];
            Assert.Contains("PAT_A - Test^Patient", patientA.TextContent);
            // The patient row counts its series' and studies' badges.
            Assert.Contains(FlagCodes.GeomNonUniformZ, Codes(patientA));
            Assert.Contains(FlagCodes.LinkUnlinked, Codes(patientA));

            // Rows carry their tree position; the test finds the nodes it wants in a tree of its own.
            var tree = new InventoryTree(context.Services.GetRequiredService<ScanSession>().Current.Scan);
            var series = tree.Patients[0].ImageSeries.Single(s => s.Series.SeriesInstanceUID == cohort.NonUniformCt);
            var imaged = series.Study;
            var orphan = tree.Patients[0].Studies.Single(s => s.ImageSeries.Count == 0);
            ByKey(page, tree.Patients[0].Key).QuerySelector(".inv-expander").Click();
            ByKey(page, imaged.Key).QuerySelector(".inv-expander").Click();
            Assert.Equal(2 + 2 + 2, page.FindAll(".inv-row").Count);

            var nonUniform = ByKey(page, series.Key);
            Assert.Equal(new[] { FlagCodes.GeomNonUniformZ }, Codes(nonUniform));
            Assert.True(SeriesFlags.IsNonUniformZ(series.Series, out string warning));
            Assert.Equal(warning, nonUniform.QuerySelector(".inv-flag").GetAttribute("title"));
            Assert.Empty(Codes(ByKey(page, imaged.ImageSeries.Single(s => s != series).Key)));
            Assert.Contains(FlagCodes.LinkMultipleFrames, Codes(ByKey(page, imaged.Key)));
            Assert.Equal(new[] { FlagCodes.LinkUnlinked }, Codes(ByKey(page, orphan.Key)));

            // The series inspector repeats the conversions' warning and lists the linked structure set.
            nonUniform.QuerySelector(".inv-label").Click();
            Assert.Equal("Series inspector", page.Find(".inv-inspector-head").TextContent);
            Assert.Equal("1, 3 mm", page.Find(".inv-gaps").TextContent);
            Assert.Equal(warning, page.Find(".inv-warning-text").TextContent);
            Assert.Contains("Replan structures · 1 ROI(s) · by frame of reference UID", page.Find(".inv-inspector").TextContent);

            // The tags tab: Core reads the series' first slice and its structure set.
            page.FindAll(".inv-tab")[1].Click();
            page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".inv-tags").Count));
            var image = page.FindAll(".inv-tags")[0].TextContent;
            Assert.Contains("Series Instance UID", image);
            Assert.Contains(cohort.NonUniformCt, image);
            Assert.Contains("CT series", image);
            Assert.Contains("Replan structures", page.FindAll(".inv-tags")[1].TextContent);
            Assert.Contains("No such file in this series.", page.Find(".inv-inspector").TextContent);
            Assert.DoesNotContain(host.Folders.Root, page.Markup, StringComparison.OrdinalIgnoreCase);

            // The study inspector shows the RT objects the tree does not, with their link rules. The
            // tags tab loads off the renderer (Task.Run) and its last render can still be finishing,
            // so the click's render may land after Click() returns: wait for it, as for any async render.
            ByKey(page, imaged.Key).QuerySelector(".inv-label").Click();
            page.WaitForAssertion(() => Assert.Equal("Study inspector", page.Find(".inv-inspector-head").TextContent), TimeSpan.FromSeconds(10));
            string study = page.Find(".inv-inspector").TextContent;
            Assert.Contains("Planning structures · 2 ROI(s) · linked to [CT] CT series, by referenced series UID", study);
            Assert.Contains("2 frames of reference", study);

            // Filter by badge: only PAT_A's unlinked study and its path show.
            page.Find(".inv-badge-filter").Change(FlagCodes.LinkUnlinked);
            Assert.Equal(2, page.FindAll(".inv-row").Count);
            Assert.All(page.FindAll(".inv-row"), r => Assert.DoesNotContain("PAT_B", r.TextContent));
            ByKey(page, orphan.Key).QuerySelector(".inv-label").Click();
            Assert.Contains("Orphan structures · 1 ROI(s) · linked to no image series in this study", page.Find(".inv-inspector").TextContent);
            Assert.Contains(StudyInspector.UnlinkedNote, page.Find(".inv-inspector").TextContent);

            // Filter by text over the patient: PAT_B's patient, study and series.
            page.Find(".inv-clear-filter").Click();
            page.Find(".inv-text").Change("pat_b");
            Assert.Equal(3, page.FindAll(".inv-row").Count);
            page.Find(".inv-text").Change("no such thing");
            Assert.Contains("Nothing matches the filter.", page.Find(".inv-no-match").TextContent);
        }

        [Fact]
        public async Task The_selection_lives_with_the_snapshot_and_renders_as_series()
        {
            await using var host = await StartAsync();
            var cohort = InventoryCohort.Write(Path.Combine(host.Folders.Data, "cohort"));
            using var context = Harness(host);
            var snapshot = await OpenSnapshotAsync(host, context);

            var page = context.Render<InventoryPage>();
            Assert.Contains("No series selected", page.Find(".inv-selection-summary").TextContent);

            // A patient's checkbox selects every image series under it.
            Row(page, "PAT_A").QuerySelector(".inv-check").Change(true);

            Assert.Equal(
                new[] { cohort.UniformCt, cohort.NonUniformCt }.OrderBy(u => u, StringComparer.Ordinal),
                snapshot.Selection.SeriesInstanceUids());
            Assert.Contains("2 of 3 image series selected.", page.Find(".inv-selection-summary").TextContent);
            string cli = page.FindAll(".cli pre").Last().TextContent;
            Assert.Equal(
                "DicomRtNifti.Cli --cohort-manifest --input archive:cohort --output output:PROJECT --series "
                + string.Join(",", snapshot.Selection.SeriesInstanceUids()) + " --dry-run",
                cli);

            // Another page in the same circuit reads the same selection.
            var again = context.Render<InventoryPage>();
            Assert.Contains("2 of 3 image series selected.", again.Find(".inv-selection-summary").TextContent);

            Row(page, "PAT_A").QuerySelector(".inv-check").Change(false);
            Assert.True(snapshot.Selection.IsEmpty);
            page.WaitForAssertion(() => Assert.Contains("No series selected", page.Find(".inv-selection-summary").TextContent));
        }

        [Fact]
        public async Task The_tag_reader_opens_only_files_under_a_data_root()
        {
            await using var host = await StartAsync();
            var cohort = InventoryCohort.Write(Path.Combine(host.Folders.Data, "cohort"));
            var reader = new InspectorTagReader(
                host.Services.GetRequiredService<DataRootResolver>(), host.Services.GetRequiredService<RootPathPresenter>());
            string inside = Directory.GetFiles(Path.Combine(cohort.Root, InventoryCohort.PatientA, "ct1"))[0];
            string outside = Path.Combine(host.Folders.State, "slice.dcm");
            File.Copy(inside, outside);

            var rows = await reader.ReadAsync(inside, new[] { "PatientID", "Modality", "PatientBirthDate" });

            Assert.Equal(new[] { "PatientID", "Modality" }, rows.Select(r => r.Keyword));
            Assert.Equal(InventoryCohort.PatientA, rows[0].Value);
            await Assert.ThrowsAsync<PathRefusedException>(() => reader.ReadAsync(outside, new[] { "PatientID" }));
            await Assert.ThrowsAsync<PathRefusedException>(() => reader.ReadAsync(inside + ".gone", new[] { "PatientID" }));
            await Assert.ThrowsAsync<PathRefusedException>(() => reader.ReadAsync(Path.Combine(cohort.Root, "..", "..", "state", "slice.dcm"), new[] { "PatientID" }));
        }

        [Fact]
        public async Task A_large_tree_is_virtualized()
        {
            await using var host = await StartAsync();
            using var context = Harness(host);
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var scan = new DicomScanResult();
            for (int i = 0; i < InventoryView.VirtualizeFrom + 50; i++)
            {
                scan.Patients.Add(new DicomPatientGroup
                {
                    PatientID = $"P{i:D4}",
                    Studies = { new DicomStudyGroup { StudyInstanceUID = $"1.2.{i}", Series = { new DicomSeriesGroup { SeriesInstanceUID = $"1.3.{i}", Modality = "CT" } } } },
                });
            }

            var store = context.Services.GetRequiredService<ScanSnapshotStore>();
            var resolver = context.Services.GetRequiredService<DataRootResolver>();
            string id = JobStore.NewId();
            store.Save(id, resolver.Resolve("archive", ""), scan, DateTimeOffset.UtcNow);
            context.Services.GetRequiredService<ScanSession>().Open(id);

            var page = context.Render<InventoryPage>();

            Assert.Single(page.FindComponents<Virtualize<DicomRtNifti.Web.Features.Inventory.InventoryRow>>());
            Assert.True(page.FindAll(".inv-row").Count < InventoryView.VirtualizeFrom);
        }

        private static string[] Codes(AngleSharp.Dom.IElement row) =>
            row.QuerySelectorAll(".inv-flag").Select(b => b.GetAttribute("data-code")).ToArray();

        private static AngleSharp.Dom.IElement ByKey(IRenderedComponent<InventoryPage> page, string key) =>
            page.Find($".inv-row[data-key='{key}']");

        private static AngleSharp.Dom.IElement Row(IRenderedComponent<InventoryPage> page, string text) =>
            page.FindAll(".inv-row").First(r => r.QuerySelector(".inv-label").TextContent.Contains(text, StringComparison.Ordinal));

        private static async Task<ScanSnapshot> OpenSnapshotAsync(JobTestHost host, BunitContext context)
        {
            var done = await JobWait.RunAsync(host.Queue, SnapshotScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            Assert.True(done.State == JobState.Succeeded, done.Reason);
            context.Services.GetRequiredService<ScanSession>().Open(done.Id);
            return context.Services.GetRequiredService<ScanSnapshotStore>().Find(done.Id);
        }

        private static Task<JobTestHost> StartAsync() =>
            JobTestHost.StartAsync(services: s =>
            {
                ScanStateServices.Add(s);
                JobServices.AddHandler<SnapshotScanJobHandler>(s);
            });

        private static BunitContext Harness(JobTestHost host)
        {
            var options = host.Services.GetRequiredService<IOptions<WebOptions>>();
            var context = ComponentHarness.Create(options.Value);
            var store = host.Services.GetRequiredService<ScanSnapshotStore>();
            context.Services.AddSingleton(options);
            context.Services.AddSingleton(host.Queue);
            context.Services.AddSingleton(host.Events);
            context.Services.AddSingleton(store);
            context.Services.AddSingleton(new ScanSession(store));
            context.Services.AddSingleton(host.Services.GetRequiredService<RootPathPresenter>());
            context.Services.AddSingleton(new InspectorTagReader(
                host.Services.GetRequiredService<DataRootResolver>(), host.Services.GetRequiredService<RootPathPresenter>()));
            context.Services.AddSingleton<ICurrentUser>(new LocalUser());
            return context;
        }
    }
}
