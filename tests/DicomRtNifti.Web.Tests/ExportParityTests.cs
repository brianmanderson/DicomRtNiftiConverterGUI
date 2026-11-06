using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DicomRtNifti.Core.CommandLine;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
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
    /// The web's forward path against the CLI (brief W4-H's definition of done, plan section 7.1):
    /// the export a session describes is planned and converted through the job service, and the
    /// command the page shows for it is run as it stands by the real CLI, as a separate process.
    /// The plan documents are byte-identical, the convert documents are byte-identical, and the two
    /// output trees hold the same files with the same bytes, manifest included; documents differ
    /// only in how an absolute path is shown, which is normalized by the web's own presenter. The
    /// CLI's run writes into another folder, the only change made to the shown command (and, under
    /// anonymization, the salt the page names <c>$PROJECT_SALT</c> is expanded as a shell would).
    /// The synthesized cohort has a CT, a structure set with two ROIs and an RT dose per patient,
    /// two spellings of one ROI across the patients, an association set, an ROI selection, a
    /// resample and a metadata sidecar, so every option kind the page sets is on the command line.
    /// </summary>
    public sealed class ExportParityTests
    {
        private const int Seconds = 180;

        [Fact]
        public async Task A_plan_and_a_conversion_from_the_web_equal_the_shown_command_run_by_the_CLI()
        {
            await using var host = await ExportFlow.StartAsync(configure: o => o.ShowAbsolutePaths = true);
            var identifiers = ExportFlow.WriteCohort(host.Folders);
            var (session, builder) = Session(host, anonymize: false);

            // Plan review: the dry run's document, and the CLI's --dry-run of the shown command.
            var plan = builder.Build(session, new NoInventorySelection(), CohortMode.Convert, dryRun: true, createSalt: true);
            Assert.True(plan.IsValid, string.Join(" ", plan.Problems));
            Assert.EndsWith(" --dry-run", plan.Cli);
            var planJob = await JobWait.RunAsync(host.Queue, PlanJobHandler.KindName, PlanJobParameters.For(plan.Parameters, survey: false), Seconds);
            Assert.True(planJob.State == JobState.Succeeded, planJob.Reason + "\n" + host.LogFile(planJob.Id));
            Assert.Equal(plan.Cli, planJob.Cli);
            Assert.Null(planJob.Project); // a dry run writes nothing, so it holds no project

            var cliPlan = await ExportFlow.RunCliAsync(ExportFlow.SplitCommand(plan.Cli));
            Assert.True(cliPlan.ExitCode == 0, cliPlan.Stderr);
            var presenter = host.Services.GetRequiredService<RootPathPresenter>();
            string webPlan = host.Queue.ReadResult(planJob.Id);
            Assert.Equal(webPlan, ExportFlow.PresentPaths(cliPlan.Stdout, presenter));
            Assert.Contains("\"SpinalCord\"", webPlan);
            Assert.False(Directory.Exists(Path.Combine(host.Folders.Output, "proj")), "the dry run wrote the project");

            // Run conversion, and the CLI's run of the same shown command into another folder.
            var convert = builder.Build(session, new NoInventorySelection(), CohortMode.Convert, createSalt: true);
            Assert.True(convert.IsValid, string.Join(" ", convert.Problems));
            var job = await JobWait.RunAsync(host.Queue, CohortConvertJobHandler.KindName, convert.Parameters, Seconds);
            Assert.True(job.State == JobState.Succeeded, job.Reason + "\n" + host.LogFile(job.Id));
            Assert.Equal(convert.Cli, job.Cli);
            Assert.Equal("2 series converted.", job.Reason);

            using var cliFolders = JobFolders.Create();
            var argv = ExportFlow.WithValue(ExportFlow.SplitCommand(convert.Cli), "--output", Path.Combine(cliFolders.Output, "proj"));
            var cli = await ExportFlow.RunCliAsync(argv);
            Assert.True(cli.ExitCode == 0, cli.Stderr);

            string web = Path.Combine(host.Folders.Output, "proj");
            AssertSameTree(web, Path.Combine(cliFolders.Output, "proj"));
            var tree = ExportFlow.Tree(web);
            Assert.Contains("export_manifest.csv", tree);
            Assert.Equal(2, tree.Count(f => f.EndsWith("/image.nii.gz", StringComparison.Ordinal)));
            Assert.Equal(2, tree.Count(f => f.Contains("SpinalCord", StringComparison.Ordinal) && f.EndsWith(".nii.gz", StringComparison.Ordinal)));
            Assert.Equal(2, tree.Count(f => f.Contains("GTV", StringComparison.Ordinal) && f.EndsWith(".nii.gz", StringComparison.Ordinal)));
            Assert.True(tree.Count(f => f.Contains("dose", StringComparison.OrdinalIgnoreCase)) == 2, string.Join("\n", tree));
            Assert.True(tree.Count(f => f.EndsWith("metadata.json", StringComparison.Ordinal)) == 2, string.Join("\n", tree));

            Assert.Equal(host.Queue.ReadResult(job.Id), ExportFlow.PresentPaths(cli.Stdout, CliPresenter(host, cliFolders)));

            // Not anonymized, the outputs name the patients; the application log never does.
            Assert.Contains(tree, f => f.StartsWith("MRN-H-001/", StringComparison.Ordinal));
            AssertNoneIn(LogText(host), identifiers, "the application log");
        }

        [Fact]
        public async Task An_anonymized_plan_and_conversion_equal_the_CLI_under_the_projects_own_salt_and_leak_nothing()
        {
            await using var host = await ExportFlow.StartAsync(configure: o => o.ShowAbsolutePaths = true);
            var identifiers = ExportFlow.WriteCohort(host.Folders, prefix: "MRN-ANON");
            var (session, builder) = Session(host, anonymize: true);

            // Before any submission the project has no key and no salt; nothing is written to show the command.
            var preview = builder.Build(session, new NoInventorySelection(), CohortMode.Convert);
            Assert.True(preview.Salt.Pending);
            Assert.Null(preview.Parameters);
            Assert.Contains("--salt $PROJECT_SALT", preview.Cli);
            string keyFile = Path.Combine(host.Folders.Keys, "proj", AnonymizationKeyLocation.FileName);
            Assert.False(File.Exists(keyFile));

            var plan = builder.Build(session, new NoInventorySelection(), CohortMode.Convert, dryRun: true, createSalt: true);
            Assert.True(plan.IsValid, string.Join(" ", plan.Problems));
            string salt = AnonymizationService.LoadKeyFile(keyFile).Salt;
            Assert.Matches(new Regex("^[0-9a-f]{32}$"), salt);
            Assert.NotEqual(AnonymizationService.DefaultSalt, salt);
            Assert.DoesNotContain(salt, plan.Cli);
            Assert.Contains("--salt $PROJECT_SALT", plan.Cli);

            var planJob = await JobWait.RunAsync(host.Queue, PlanJobHandler.KindName, PlanJobParameters.For(plan.Parameters, survey: false), Seconds);
            Assert.True(planJob.State == JobState.Succeeded, planJob.Reason + "\n" + host.LogFile(planJob.Id));
            Assert.DoesNotContain(salt, planJob.Cli);
            Assert.Equal(plan.Cli, planJob.Cli);

            var cliPlan = await ExportFlow.RunCliAsync(ExportFlow.WithValue(ExportFlow.SplitCommand(plan.Cli), "--salt", salt));
            Assert.True(cliPlan.ExitCode == 0, cliPlan.Stderr);
            var presenter = host.Services.GetRequiredService<RootPathPresenter>();
            string webPlan = host.Queue.ReadResult(planJob.Id);
            Assert.Equal(webPlan, ExportFlow.PresentPaths(cliPlan.Stdout, presenter));

            // The second submission reads the same salt back from the key.
            var convert = builder.Build(session, new NoInventorySelection(), CohortMode.Convert, createSalt: true);
            Assert.Equal(salt, convert.Salt.Salt);
            var job = await JobWait.RunAsync(host.Queue, CohortConvertJobHandler.KindName, convert.Parameters, Seconds);
            Assert.True(job.State == JobState.Succeeded, job.Reason + "\n" + host.LogFile(job.Id));
            Assert.Equal(salt, AnonymizationService.LoadKeyFile(keyFile).Salt);

            using var cliFolders = JobFolders.Create();
            var argv = ExportFlow.SplitCommand(convert.Cli);
            argv = ExportFlow.WithValue(argv, "--output", Path.Combine(cliFolders.Output, "proj"));
            argv = ExportFlow.WithValue(argv, "--key-path", Path.Combine(cliFolders.Keys, "proj", AnonymizationKeyLocation.FileName));
            argv = ExportFlow.WithValue(argv, "--salt", salt);
            var cli = await ExportFlow.RunCliAsync(argv);
            Assert.True(cli.ExitCode == 0, cli.Stderr);

            string web = Path.Combine(host.Folders.Output, "proj");
            AssertSameTree(web, Path.Combine(cliFolders.Output, "proj"));
            Assert.Equal(File.ReadAllBytes(keyFile), File.ReadAllBytes(Path.Combine(cliFolders.Keys, "proj", AnonymizationKeyLocation.FileName)));
            string webConvert = host.Queue.ReadResult(job.Id);
            Assert.Equal(webConvert, ExportFlow.PresentPaths(cli.Stdout, CliPresenter(host, cliFolders)));

            // PHI: the anonymized outputs (names and contents, NIfTI decompressed), the documents
            // the web emitted and the application log carry no identifier; the log no salt either.
            AssertNoneIn(string.Join("\n", ExportFlow.Tree(web)), identifiers, "the output file names");
            foreach (string file in ExportFlow.Tree(web))
                AssertNoneIn(Contents(Path.Combine(web, file)), identifiers, file);
            AssertNoneIn(webPlan, identifiers, "the plan document");
            AssertNoneIn(webConvert, identifiers, "the convert document");
            string log = LogText(host);
            AssertNoneIn(log, identifiers, "the application log");
            Assert.DoesNotContain(salt, log);
            Assert.Empty(Directory.GetFiles(host.Folders.Output, AnonymizationKeyLocation.FileName, SearchOption.AllDirectories));
        }

        [Fact]
        public async Task The_ROI_name_analysis_equals_the_CLIs_cohort_analyze()
        {
            await using var host = await ExportFlow.StartAsync(configure: o => o.ShowAbsolutePaths = true);
            ExportFlow.WriteCohort(host.Folders);
            Session(host, anonymize: false);

            var job = await JobWait.RunAsync(host.Queue, RoiAnalysisJobHandler.KindName,
                new RoiAnalysisJobParameters("archive", "cohort", ProjectStore.AssociationsFile("proj", "hn")), Seconds);
            Assert.True(job.State == JobState.Succeeded, job.Reason + "\n" + host.LogFile(job.Id));
            Assert.Contains("--cohort-analyze", job.Cli);
            Assert.Contains("--associations", job.Cli);

            var cli = await ExportFlow.RunCliAsync(ExportFlow.SplitCommand(job.Cli));
            Assert.True(cli.ExitCode == 0, cli.Stderr);
            string web = host.Queue.ReadResult(job.Id);
            Assert.Equal(web, ExportFlow.PresentPaths(cli.Stdout, host.Services.GetRequiredService<RootPathPresenter>()));
            Assert.Contains("\"roi_naming\"", web);
            Assert.Contains("\"associations\": \"roi_associations.json\"", web);
            Assert.NotNull(host.Services.GetRequiredService<ICohortScanSource>().Find("archive", "cohort"));
        }

        /// <summary>
        /// The session the parity runs share: archive:cohort into project <c>proj</c>, the saved set
        /// <c>hn</c> joining the two spellings of the cord, both ROIs selected, a 2 x 2 x 1 mm
        /// resample, and an image metadata sidecar with a computed value.
        /// </summary>
        private static (ExportSession Session, ExportCommandBuilder Builder) Session(JobTestHost host, bool anonymize)
        {
            host.Services.GetRequiredService<ProjectStore>().SaveAssociations("proj", "hn", new[]
            {
                new RoiAssociation { CanonicalName = "SpinalCord", Aliases = new List<string> { "Spinal_Cord", "spinal cord" } },
            });
            var session = new ExportSession
            {
                InputRootId = "archive",
                InputPath = "cohort",
                Project = "proj",
                AssociationSet = "hn",
            };
            session.Options.RoiSelection = new List<string> { "SpinalCord", "GTV" };
            session.Options.OutputSpacing = new[] { 2.0, 2.0, 1.0 };
            session.Options.ImageKeywords = new List<string> { "Modality", "@VoxelSize" };
            session.Options.Anonymize = anonymize;
            return (session, host.Services.GetRequiredService<ExportCommandBuilder>());
        }

        /// <summary>How the web presents paths, over the CLI's folders: its output and key roots, and the shared input root.</summary>
        private static RootPathPresenter CliPresenter(JobTestHost host, JobFolders cliFolders)
        {
            var options = cliFolders.Options();
            options.DataRoots[0].Path = host.Folders.Data;
            return new RootPathPresenter(DataRoots.From(options), Options.Create(options));
        }

        private static void AssertSameTree(string web, string cli)
        {
            var files = ExportFlow.Tree(web);
            Assert.Equal(files, ExportFlow.Tree(cli));
            Assert.NotEmpty(files);
            foreach (string file in files)
            {
                Assert.True(File.ReadAllBytes(Path.Combine(web, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(cli, file))),
                    file + " differs between the web's and the CLI's output.");
            }
        }

        private static string LogText(JobTestHost host) => string.Join("\n", host.Logs.Logs.Select(l =>
            $"{l.Category} {l.Message} {string.Join(" ", (l.State ?? Array.Empty<KeyValuePair<string, object>>()).Select(s => s.Value))} {l.Exception}"));

        /// <summary>A file as text, decompressed when it is gzip, so a header field is seen as written.</summary>
        private static string Contents(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var output = new MemoryStream();
                input.CopyTo(output);
                bytes = output.ToArray();
            }

            return Encoding.Latin1.GetString(bytes);
        }

        private static void AssertNoneIn(string text, IEnumerable<string> identifiers, string where)
        {
            foreach (string identifier in identifiers)
                Assert.False(text.Contains(identifier, StringComparison.Ordinal), $"{where} contains the identifier '{identifier}'.");
        }
    }
}
