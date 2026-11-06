using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Web.Features.DataSources;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Features.Inventory;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Web.State;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The seam between Inventory (W4-G) and Export setup (W4-H): Export reads the selection through
    /// <see cref="IInventorySelection"/> and registers a stand-in; the Inventory's registration wins
    /// whichever feature registers first, and the series ticked in the tree reach Core's
    /// <c>CohortExportOptions.SeriesInstanceUids</c> through Export's own command builder.
    /// </summary>
    public sealed class InventoryExportSelectionTests
    {
        [Fact]
        public void The_inventory_selection_wins_in_either_registration_order()
        {
            var configuration = new ConfigurationBuilder().Build();
            foreach (bool exportFirst in new[] { true, false })
            {
                var services = new ServiceCollection();
                if (exportFirst)
                    new ExportFeature().ConfigureServices(services, configuration);
                new InventoryFeature().ConfigureServices(services, configuration);
                if (!exportFirst)
                    new ExportFeature().ConfigureServices(services, configuration);

                // The container resolves the last registration of a service.
                var last = services.Last(d => d.ServiceType == typeof(IInventorySelection));
                Assert.Equal(typeof(InventoryExportSelection), last.ImplementationType);
                Assert.Equal(ServiceLifetime.Scoped, last.Lifetime);
            }
        }

        [Fact]
        public async Task The_series_ticked_in_the_inventory_reach_the_export_options()
        {
            using var folders = JobFolders.Create();
            var cohort = InventoryCohort.Write(Path.Combine(folders.Data, "cohort"));
            using var factory = new WebAppFactory(JobWait.Settings(folders));
            var queue = factory.Services.GetRequiredService<JobQueue>();
            var done = await JobWait.RunAsync(queue, SnapshotScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            Assert.Equal(JobState.Succeeded, done.State);

            using var scope = factory.Services.CreateScope();
            var selection = scope.ServiceProvider.GetRequiredService<IInventorySelection>();
            Assert.IsType<InventoryExportSelection>(selection);
            scope.ServiceProvider.GetRequiredService<ScanSession>().Open(done.Id);
            var snapshot = scope.ServiceProvider.GetRequiredService<IScanSnapshots>().Find(done.Id);

            // Nothing ticked: the whole scan, as on the command line.
            Assert.Empty(selection.SeriesInstanceUids);
            Assert.Empty(selection.PatientIds);
            Assert.Empty(selection.StructureSetUids);

            snapshot.Selection.Set(new[] { cohort.UniformCt, cohort.NonUniformCt }, true);
            Assert.Equal(snapshot.Selection.SeriesInstanceUids(), selection.SeriesInstanceUids);

            // Export's own builder puts them in the options it renders and the job it submits.
            var session = scope.ServiceProvider.GetRequiredService<ExportSession>();
            session.InputRootId = "archive";
            session.InputPath = "cohort";
            session.Project = "proj";
            var command = scope.ServiceProvider.GetRequiredService<ExportCommandBuilder>()
                .Build(session, selection, CohortMode.Manifest, dryRun: true);
            Assert.True(command.IsValid, string.Join("; ", command.Problems));
            string series = string.Join(",", snapshot.Selection.SeriesInstanceUids());
            Assert.Contains("--series " + series, command.Cli);
            Assert.Contains("--series", command.Parameters.Arguments);
            Assert.Contains(series, command.Parameters.Arguments);
        }
    }
}
