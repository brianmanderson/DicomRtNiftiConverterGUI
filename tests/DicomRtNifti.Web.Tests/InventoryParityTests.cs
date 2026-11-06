using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Web.Features.DataSources;
using DicomRtNifti.Web.Features.Inventory;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The Data sources scan and the Inventory against the CLI (plan section 7.1, CLI parity), in
    /// the web app as Program builds it, over a synthesized cohort: the scan job's result is the
    /// <c>--cohort-scan</c> document byte for byte, the same bytes the built-in <c>scan</c> kind
    /// returns; the snapshot it leaves is the tree the Inventory shows, and that tree holds what
    /// the document lists (every patient, study and image series, their geometry, link rules and
    /// unlinked objects); and both pages render it with root tokens, never a server path.
    /// </summary>
    public sealed class InventoryParityTests
    {
        [Fact]
        public async Task The_scan_job_returns_the_cohort_scan_document_and_keeps_a_snapshot()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            using var factory = new WebAppFactory(JobWait.Settings(folders));
            var queue = factory.Services.GetRequiredService<JobQueue>();

            var submission = queue.Submit(SnapshotScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"), "local:tester");
            Assert.True(submission.Accepted, submission.Refusal);
            Assert.Equal("Scan archive:cohort", submission.Job.Title);
            Assert.Equal("DicomRtNifti.Cli --cohort-scan --input archive:cohort", submission.Job.Cli);
            Assert.Equal(JobLane.Light, submission.Job.Lane);
            var done = await JobWait.FinishedAsync(queue, submission.Job.Id);
            Assert.True(done.State == JobState.Succeeded, done.Reason);
            Assert.Equal("files", done.Progress.Unit);
            Assert.True(done.Progress.TotalKnown);

            // Byte for byte what Core builds for the folder, and what the built-in kind returns.
            string result = queue.ReadResult(done.Id);
            var rescan = await ScanSnapshotStoreTests.Scan(cohort.Root);
            Assert.Equal(CohortDocuments.Serialize(CohortScanDocument.Build(rescan, cohort.Root, _ => "archive:cohort")), result);
            var builtIn = await JobWait.RunAsync(queue, ScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            Assert.Equal(JobState.Succeeded, builtIn.State);
            Assert.Equal(result, queue.ReadResult(builtIn.Id));
            Assert.Equal(builtIn.Title, done.Title);
            Assert.Equal(builtIn.Cli, done.Cli);
            Assert.Equal(builtIn.Reason, done.Reason);

            // Later pages read the snapshot through the read-only interface, which is the store.
            Assert.Same(factory.Services.GetRequiredService<ScanSnapshotStore>(), factory.Services.GetRequiredService<IScanSnapshots>());
            var snapshot = factory.Services.GetRequiredService<IScanSnapshots>().Find(done.Id);
            Assert.NotNull(snapshot);
            Assert.Equal("archive:cohort", snapshot.Folder.Presented);
            Assert.True(File.Exists(Path.Combine(folders.State, ScanSnapshotStore.ScansFolder, done.Id + ".json")));

            AssertTreeEqualsDocument(new InventoryTree(snapshot.Scan), result);
        }

        [Fact]
        public async Task The_inventory_tree_holds_what_the_cohort_scan_document_lists()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var scan = await ScanSnapshotStoreTests.Scan(cohort.Root);

            var tree = new InventoryTree(scan);
            string document = CohortDocuments.Serialize(CohortScanDocument.Build(scan, cohort.Root, CohortDocuments.Identity));

            AssertTreeEqualsDocument(tree, document);
            // The cohort reaches every part of the comparison.
            Assert.Equal(3, tree.SeriesCount);
            Assert.Contains(tree.Patients.SelectMany(p => p.Studies), s => s.Objects.Any(o => o.IsUnlinked));
            Assert.Contains(tree.Patients.SelectMany(p => p.ImageSeries), s => s.Geometry is { Uniform: false });
        }

        [Fact]
        public async Task Both_pages_render_the_scan_with_root_tokens_only()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            using var factory = new WebAppFactory(JobWait.Settings(folders));
            var queue = factory.Services.GetRequiredService<JobQueue>();
            var done = await JobWait.RunAsync(queue, SnapshotScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            Assert.Equal(JobState.Succeeded, done.State);
            using var client = factory.CreateClient();

            var sources = await client.GetAsync("/data-sources");
            string sourcesHtml = await sources.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, sources.StatusCode);
            Assert.Contains("Scan archive:cohort", sourcesHtml);
            Assert.Contains("Open in Inventory", sourcesHtml);
            Assert.Contains("--cohort-scan --input archive: --json-out scan.json", sourcesHtml);

            // The latest snapshot is the one in view, with no choice made in this browser.
            var inventory = await client.GetAsync("/inventory");
            string html = await inventory.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
            Assert.Contains("archive:cohort", html);
            Assert.Contains("PAT_A - Test^Patient", html);
            Assert.Contains("DicomRtNifti.Cli --cohort-scan --input archive:cohort", html);
            Assert.Contains("DicomRtNifti.Cli --cohort-analyze --input archive:cohort", html);
            Assert.Contains("Patient inspector", html);

            foreach (string page in new[] { sourcesHtml, html })
            {
                Assert.DoesNotContain(folders.Root, page, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(folders.Root.Replace('\\', '/'), page, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Every patient, study and image series of the document is a node of the tree, in the same
        /// order, with the same facts; and the tree's unlinked RT objects are the document's.
        /// </summary>
        internal static void AssertTreeEqualsDocument(InventoryTree tree, string json)
        {
            using var document = JsonDocument.Parse(json);
            var patients = document.RootElement.GetProperty("patients").EnumerateArray().ToList();
            Assert.Equal(patients.Count, tree.Patients.Count);
            Assert.Equal(document.RootElement.GetProperty("scanned_files").GetInt32(), tree.Scan.TotalFileCount);
            Assert.Equal(document.RootElement.GetProperty("unreadable_files").GetInt32(), tree.Scan.SkippedErrorCount);

            for (int p = 0; p < patients.Count; p++)
            {
                var patient = tree.Patients[p];
                Assert.Equal(patients[p].GetProperty("patient_id").GetString(), patient.Patient.PatientID);
                var studies = patients[p].GetProperty("studies").EnumerateArray().ToList();
                Assert.Equal(studies.Count, patient.Studies.Count);
                for (int s = 0; s < studies.Count; s++)
                {
                    var study = patient.Studies[s];
                    Assert.Equal(studies[s].GetProperty("study_instance_uid").GetString(), study.Study.StudyInstanceUID);

                    var series = studies[s].GetProperty("series").EnumerateArray().ToList();
                    Assert.Equal(series.Count, study.ImageSeries.Count);
                    for (int i = 0; i < series.Count; i++)
                        AssertSeries(series[i], study.ImageSeries[i]);

                    // The document lists RT objects that matched no image series; the tree badges
                    // the RTSTRUCT and RTDOSE among them unlinked, and lists the rest (a plan) only.
                    var unlinked = studies[s].GetProperty("unlinked").EnumerateArray()
                        .Where(u => u.GetProperty("modality").GetString() is "RTSTRUCT" or "RTDOSE")
                        .Select(u => u.GetProperty("series_instance_uid").GetString())
                        .ToList();
                    Assert.Equal(unlinked, study.Objects.Where(o => o.IsUnlinked).Select(o => o.Series.SeriesInstanceUID).ToList());
                }
            }
        }

        private static void AssertSeries(JsonElement expected, SeriesNode node)
        {
            var s = node.Series;
            Assert.Equal(expected.GetProperty("series_instance_uid").GetString(), s.SeriesInstanceUID);
            Assert.Equal(expected.GetProperty("modality").GetString(), s.Modality);
            Assert.Equal(expected.GetProperty("series_description").GetString(), s.SeriesDescription);
            Assert.Equal(expected.GetProperty("frame_of_reference_uid").GetString(), s.FrameOfReferenceUID);
            Assert.Equal(expected.GetProperty("instance_count").GetInt32(), s.FilePaths.Count);
            Assert.Equal(NullableDouble(expected.GetProperty("slice_spacing")), node.Geometry?.SliceSpacing);
            Assert.Equal(NullableBool(expected.GetProperty("slice_spacing_uniform")), node.Geometry?.Uniform);
            Assert.Equal(Doubles(expected.GetProperty("slice_spacing_values")), node.Geometry?.DistinctGaps);
            Assert.Equal(
                expected.GetProperty("unpositioned_slices").EnumerateArray().Select(e => e.GetString()).ToList(),
                s.UnpositionedSlices);

            var structs = expected.GetProperty("linked_rtstructs").EnumerateArray().ToList();
            Assert.Equal(structs.Count, node.LinkedStructs.Count);
            for (int i = 0; i < structs.Count; i++)
            {
                Assert.Equal(structs[i].GetProperty("series_instance_uid").GetString(), node.LinkedStructs[i].SeriesInstanceUID);
                Assert.Equal(structs[i].GetProperty("match_rule").GetString(), node.LinkedStructs[i].LinkMatchRule.ToString());
                Assert.Equal(
                    structs[i].GetProperty("roi_names").EnumerateArray().Select(e => e.GetString()).ToList(),
                    node.LinkedStructs[i].RoiNames);
            }

            var doses = expected.GetProperty("linked_rtdoses").EnumerateArray().ToList();
            Assert.Equal(doses.Count, node.LinkedDoses.Count);
            for (int i = 0; i < doses.Count; i++)
            {
                Assert.Equal(doses[i].GetProperty("series_instance_uid").GetString(), node.LinkedDoses[i].SeriesInstanceUID);
                Assert.Equal(doses[i].GetProperty("match_rule").GetString(), node.LinkedDoses[i].LinkMatchRule.ToString());
            }
        }

        private static double? NullableDouble(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();

        private static bool? NullableBool(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetBoolean();

        private static double[] Doubles(JsonElement e) =>
            e.ValueKind == JsonValueKind.Null ? null : e.EnumerateArray().Select(x => x.GetDouble()).ToArray();
    }
}
