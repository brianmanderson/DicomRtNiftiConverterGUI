using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Analysis;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The scan snapshot round trip (plan section 4.2): a tree written by the store and read back
    /// keeps its aliases (an image series' linked RT objects are the study's own members), so
    /// <c>BuildPlan</c> on the restored tree produces exactly the plan it produces on the original,
    /// with and without a series selection, in the same process and after a restart. A plain
    /// serializer loses the aliases, and the Inventory's unlinked badge (which asks, by reference,
    /// which image series list an RT object) then flags every linked object: the alias test is
    /// not vacuous.
    /// </summary>
    public sealed class ScanSnapshotStoreTests
    {
        [Fact]
        public async Task A_restored_tree_keeps_its_aliases()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var scan = await Scan(cohort.Root);

            var restored = ScanSnapshotStore.DeserializeScan(ScanSnapshotStore.SerializeScan(scan));

            var study = restored.Patients.Single(p => p.PatientID == InventoryCohort.PatientA).Studies
                .Single(s => s.Series.Any(x => x.SeriesInstanceUID == cohort.UniformCt));
            var ct = study.Series.Single(s => s.SeriesInstanceUID == cohort.UniformCt);
            var structure = study.Series.Single(s => s.SeriesInstanceUID == cohort.StructByUid);
            var dose = study.Series.Single(s => s.SeriesInstanceUID == cohort.Dose);
            Assert.Same(structure, Assert.Single(ct.LinkedRtStructs));
            Assert.Same(structure, ct.LinkedRtStruct);
            Assert.Same(dose, Assert.Single(ct.LinkedRtDoses));
            Assert.Same(dose, ct.LinkedRtDose);
            Assert.Equal(RtLinkMatchRule.ReferencedSeriesUid, structure.LinkMatchRule);
            Assert.Equal(new[] { "GTV", "Lung_L" }, structure.RoiNames);
            // Written again, it is the same file.
            Assert.Equal(ScanSnapshotStore.SerializeScan(scan), ScanSnapshotStore.SerializeScan(restored));
        }

        [Fact]
        public async Task BuildPlan_on_a_restored_tree_equals_BuildPlan_on_the_original()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var scan = await Scan(cohort.Root);
            var restored = ScanSnapshotStore.DeserializeScan(ScanSnapshotStore.SerializeScan(scan));

            foreach (var selection in Selections(cohort))
            {
                var options = Options(cohort, folders, selection);
                string expected = Plan(scan, options);
                Assert.Equal(expected, Plan(restored, options));
                Assert.Equal(ScanDocument(scan, cohort.Root), ScanDocument(restored, cohort.Root));
            }

            // Not vacuous: the plan holds every image series.
            using var plan = JsonDocument.Parse(Plan(restored, Options(cohort, folders, new List<string>())));
            Assert.Equal(3, plan.RootElement.GetProperty("series_count").GetInt32());
        }

        [Fact]
        public async Task A_plain_serializer_loses_the_aliases_and_the_unlinked_badge_goes_wrong()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var scan = await Scan(cohort.Root);

            var plain = JsonSerializer.Deserialize<DicomScanResult>(JsonSerializer.Serialize(scan));
            var preserved = ScanSnapshotStore.DeserializeScan(ScanSnapshotStore.SerializeScan(scan));

            // SeriesFlags.IsUnlinked asks which image series list the RT object, by reference.
            Assert.Equal(new[] { cohort.UnlinkedStruct }, Unlinked(scan));
            Assert.Equal(Unlinked(scan), Unlinked(preserved));
            // Without the aliases every linked object reads as linked to nothing. (BuildPlan itself
            // walks each image series' own link lists, so its plan does not show the loss.)
            var lost = Unlinked(plain);
            Assert.Contains(cohort.StructByUid, lost);
            Assert.Contains(cohort.StructByFrame, lost);
            Assert.Contains(cohort.Dose, lost);
            Assert.Contains(cohort.UnlinkedStruct, lost);
        }

        private static List<string> Unlinked(DicomScanResult scan) =>
            scan.Patients.SelectMany(p => p.Studies)
                .SelectMany(study => study.Series.Where(rt => SeriesFlags.IsUnlinked(study, rt)).Select(rt => rt.SeriesInstanceUID))
                .ToList();

        [Fact]
        public async Task A_snapshot_survives_a_restart_and_plans_the_same()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var scan = await Scan(cohort.Root);
            string id = JobStore.NewId();
            var first = Store(folders, out var resolver);
            var folder = resolver.Resolve("archive", "cohort");

            first.Save(id, folder, scan, new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

            Assert.True(File.Exists(Path.Combine(folders.State, ScanSnapshotStore.ScansFolder, id + ".json")));
            Assert.Same(scan, first.Find(id).Scan);

            // A new store over the same folders: a restart. It reads the file back.
            var second = Store(folders, out _);
            var restored = second.Find(id);
            Assert.NotNull(restored);
            Assert.NotSame(scan, restored.Scan);
            Assert.Equal("archive:cohort", restored.Folder.Presented);
            Assert.Equal(folder.FullPath, restored.Folder.FullPath);
            Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), restored.ScannedUtc);
            foreach (var selection in Selections(cohort))
            {
                var options = Options(cohort, folders, selection);
                Assert.Equal(Plan(scan, options), Plan(restored.Scan, options));
            }

            // The file names the folder by root and relative path, never by its server path.
            string json = File.ReadAllText(Path.Combine(folders.State, ScanSnapshotStore.ScansFolder, id + ".json"));
            using var file = JsonDocument.Parse(json);
            Assert.Equal("archive", file.RootElement.GetProperty("RootId").GetString());
            Assert.Equal("cohort", file.RootElement.GetProperty("RelativePath").GetString());
        }

        [Fact]
        public void A_snapshot_id_is_a_job_id_and_an_unknown_one_finds_nothing()
        {
            using var folders = JobFolders.Create();
            var store = Store(folders, out var resolver);

            Assert.Null(store.Find(null));
            Assert.Null(store.Find("../../etc/passwd"));
            Assert.Null(store.Find(JobStore.NewId()));
            Assert.Throws<ArgumentException>(() => store.Save("not-an-id", resolver.Resolve("archive", ""), new DicomScanResult(), DateTimeOffset.UtcNow));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("../state")]
        [InlineData(@"..\state")]
        [InlineData("scans/0123456789abcdef0123456789abcdef")]
        [InlineData(@"scans\0123456789abcdef0123456789abcdef")]
        [InlineData("0123456789abcdef0123456789abcdef/..")]
        [InlineData("/0123456789abcdef0123456789abcdef")]
        public void A_snapshot_id_with_a_separator_or_dot_dot_is_refused(string id)
        {
            using var folders = JobFolders.Create();
            var store = Store(folders, out var resolver);

            Assert.Null(store.Find(id));
            Assert.Throws<ArgumentException>(() => store.Save(id, resolver.Resolve("archive", ""), new DicomScanResult(), DateTimeOffset.UtcNow));
            Assert.False(Directory.Exists(Path.Combine(folders.State, ScanSnapshotStore.ScansFolder)));
        }

        [Fact]
        public async Task A_snapshot_whose_root_is_gone_is_not_loaded()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            string id = JobStore.NewId();
            var store = Store(folders, out var resolver);
            store.Save(id, resolver.Resolve("archive", "cohort"), await Scan(cohort.Root), DateTimeOffset.UtcNow);

            var options = folders.Options();
            options.DataRoots[0].Id = "renamed";
            var roots = DataRoots.From(options);
            var after = new ScanSnapshotStore(roots, new DataRootResolver(roots), NullLogger<ScanSnapshotStore>.Instance);

            Assert.Null(after.Find(id));
        }

        [Fact]
        public async Task Without_a_state_root_snapshots_are_kept_in_memory_only()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            var options = folders.Options();
            options.StateRoot = null;
            var roots = DataRoots.From(options);
            var resolver = new DataRootResolver(roots);
            var store = new ScanSnapshotStore(roots, resolver, NullLogger<ScanSnapshotStore>.Instance);
            string id = JobStore.NewId();

            Assert.False(store.IsPersistent);
            store.Save(id, resolver.Resolve("archive", "cohort"), await Scan(cohort.Root), DateTimeOffset.UtcNow);

            Assert.NotNull(store.Find(id));
            Assert.Equal(id, store.LatestId);
            Assert.Null(new ScanSnapshotStore(roots, resolver, NullLogger<ScanSnapshotStore>.Instance).Find(id));
        }

        [Fact]
        public void The_selection_is_the_series_list_Core_takes()
        {
            var selection = new ScanSelection();
            int changes = 0;
            selection.Changed += () => changes++;

            selection.Set(new[] { "1.2.3", "1.2.10", "1.2.3" }, true);
            selection.Set(new[] { "1.2.4" }, true);
            selection.Set(new[] { "1.2.4" }, false);

            Assert.Equal(new[] { "1.2.10", "1.2.3" }, selection.SeriesInstanceUids());
            Assert.True(selection.Contains("1.2.3"));
            Assert.Equal(3, changes);
            selection.Clear();
            Assert.True(selection.IsEmpty);
        }

        internal static Task<DicomScanResult> Scan(string folder) =>
            new DicomScannerService().ScanFolderAsync(folder, null, CancellationToken.None);

        private static IEnumerable<List<string>> Selections(InventoryCohort cohort)
        {
            yield return new List<string>();
            yield return new List<string> { cohort.UniformCt };
            yield return new List<string> { cohort.NonUniformCt, cohort.UniformCt };
        }

        private static CohortExportOptions Options(InventoryCohort cohort, JobFolders folders, List<string> series) => new CohortExportOptions
        {
            InputRoot = cohort.Root,
            OutputRoot = Path.Combine(folders.Output, "proj"),
            SeriesInstanceUids = series,
        };

        private static string Plan(DicomScanResult scan, CohortExportOptions options)
        {
            var plan = CohortExportService.BuildPlan(scan, options, null);
            return CohortDocuments.Serialize(CohortPlanDocument.Build(CohortMode.Convert, plan, options, null, null, CohortDocuments.Identity));
        }

        private static string ScanDocument(DicomScanResult scan, string root) =>
            CohortDocuments.Serialize(CohortScanDocument.Build(scan, root, CohortDocuments.Identity));

        private static ScanSnapshotStore Store(JobFolders folders, out DataRootResolver resolver)
        {
            var roots = new DataRoots(Microsoft.Extensions.Options.Options.Create(folders.Options()));
            resolver = new DataRootResolver(roots);
            return new ScanSnapshotStore(roots, resolver, NullLogger<ScanSnapshotStore>.Instance);
        }
    }
}
