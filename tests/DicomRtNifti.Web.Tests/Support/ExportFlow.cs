using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DicomRtNifti.Core.Documents;
using DicomRtNifti.Core.Tests;
using DicomRtNifti.Web.Features.Curation;
using DicomRtNifti.Web.Features.Export;
using DicomRtNifti.Web.Features.Plan;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// What the forward-path tests share: a job host with the Curation, Export and Plan features'
    /// services (their job kinds included), a synthesized cohort whose identifiers are known, the
    /// real CLI run as a separate process from the build output, and the one normalization a CLI
    /// document needs before it is compared with the web's: its absolute paths shown as root tokens,
    /// by the presenter the web uses (plan section 7.1, "input_root presented the same way on both
    /// sides"). Nothing else in a document is touched.
    /// </summary>
    internal static class ExportFlow
    {
        /// <summary>The ROI names each synthesized patient carries: two spellings of one structure, and a target.</summary>
        public static readonly string[][] RoiNames =
        {
            new[] { "Spinal_Cord", "GTV" },
            new[] { "spinal cord", "GTV" },
        };

        /// <summary>A job host whose job service knows this feature group's kinds, folders under a fresh temp tree.</summary>
        public static Task<JobTestHost> StartAsync(JobFolders folders = null, Action<WebOptions> configure = null) =>
            JobTestHost.StartAsync(folders, configure, services =>
            {
                var configuration = new ConfigurationBuilder().Build();
                new ExportFeature().ConfigureServices(services, configuration);
                new CurationFeature().ConfigureServices(services, configuration);
                new PlanFeature().ConfigureServices(services, configuration);
            });

        /// <summary>
        /// Writes <c>archive:cohort</c>: one patient per entry of <see cref="RoiNames"/>, each a
        /// 16x16x4 CT, a structure set with square contours, and an RT dose on the CT's frame.
        /// Returns every identifier written (MRNs, the patient name, study, series and frame UIDs).
        /// </summary>
        public static List<string> WriteCohort(JobFolders folders, string prefix = "MRN-H")
        {
            var identifiers = new List<string> { "Test^Patient" };
            string cohort = Path.Combine(folders.Data, "cohort");
            for (int p = 0; p < RoiNames.Length; p++)
            {
                string mrn = $"{prefix}-{p + 1:D3}";
                string dir = Path.Combine(cohort, mrn);
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                string structureSet = DicomTestData.NewUid(), dose = DicomTestData.NewUid();
                DicomTestData.WriteCtSeriesWithPixels(dir, series, frame, sliceCount: 4, size: 16, studyUid: study, patientId: mrn);
                DicomTestData.WriteRtStructWithContours(dir, "rtstruct.dcm", study, structureSet, frame,
                    new[]
                    {
                        new KeyValuePair<string, double[]>(RoiNames[p][0], DicomTestData.SquareContour(3, 10, 1)),
                        new KeyValuePair<string, double[]>(RoiNames[p][1], DicomTestData.SquareContour(5, 8, 2)),
                    },
                    patientId: mrn, referencedSeriesUid: series);
                DicomTestData.WriteRtDose32Bit(dir, "rtdose.dcm", mrn, study, dose, frame,
                    doseGridScaling: 0.001, storedValues: new uint[] { 1000, 2000, 3000, 4000 },
                    pixelSpacing: new[] { 4.0, 4.0 });
                identifiers.AddRange(new[] { mrn, study, series, frame, structureSet, dose });
            }

            return identifiers;
        }

        /// <summary>The CLI as the solution build left it, next to its staged SimpleITK native.</summary>
        public static string CliDll
        {
            get
            {
                string configuration = typeof(ExportFlow).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
                string dll = Path.Combine(RepoPaths.Root, "src", "DicomRtNifti.Cli", "bin", configuration, "net10.0", "DicomRtNifti.Cli.dll");
                if (!File.Exists(dll))
                    throw new InvalidOperationException($"The CLI is not built ({dll}); build DicomRtNifti.sln -c {configuration} first.");
                return dll;
            }
        }

        public sealed record CliRun(int ExitCode, string Stdout, string Stderr);

        /// <summary>Runs <c>dotnet DicomRtNifti.Cli.dll</c> with <paramref name="argv"/>, each argument passed as it is.</summary>
        public static async Task<CliRun> RunCliAsync(IEnumerable<string> argv)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(CliDll);
            foreach (string arg in argv)
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new CliRun(process.ExitCode, await stdout, await stderr);
        }

        /// <summary>
        /// Splits a command line the way <see cref="CommandLineText.Join"/> quotes it (and a POSIX
        /// shell reads it): whitespace separates, double quotes group, <c>\"</c> is a quote.
        /// The program name is dropped.
        /// </summary>
        public static List<string> SplitCommand(string commandLine)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            bool quoted = false, inWord = false;
            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (quoted && c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    word.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                    inWord = true;
                }
                else if (!quoted && char.IsWhiteSpace(c))
                {
                    if (inWord)
                        words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
                else
                {
                    word.Append(c);
                    inWord = true;
                }
            }

            if (inWord)
                words.Add(word.ToString());
            Assert(words.Count > 0 && words[0] == CommandLineText.Program, "not a DicomRtNifti.Cli command: " + commandLine);
            return words.Skip(1).ToList();
        }

        /// <summary>
        /// <paramref name="argv"/> with the value after <paramref name="flag"/> replaced, as a user
        /// pointing the shown command at another folder (or expanding <c>$PROJECT_SALT</c>) would.
        /// </summary>
        public static List<string> WithValue(IReadOnlyList<string> argv, string flag, string value)
        {
            var copy = argv.ToList();
            int at = copy.FindIndex(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            Assert(at >= 0 && at + 1 < copy.Count, flag + " is not in the command");
            copy[at + 1] = value;
            return copy;
        }

        /// <summary>
        /// A document the CLI printed, with every absolute path in it shown as
        /// <paramref name="presenter"/> shows it, byte for byte otherwise: each such string value is
        /// replaced where it stands, in the serializer's own escaping.
        /// </summary>
        public static string PresentPaths(string cliJson, RootPathPresenter presenter)
        {
            string json = cliJson.TrimEnd('\r', '\n');
            var paths = JToken.Parse(json).SelectTokens("$..*")
                .Where(t => t.Type == JTokenType.String)
                .Select(t => (string)t)
                .Where(s => Path.IsPathFullyQualified(s))
                .Distinct()
                .OrderByDescending(s => s.Length);
            foreach (string path in paths)
                json = json.Replace(CohortDocuments.Serialize(path), CohortDocuments.Serialize(presenter.Present(path)));
            return json;
        }

        /// <summary>Every file under <paramref name="root"/>, relative, '/'-separated, sorted.</summary>
        public static List<string> Tree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
