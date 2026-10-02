using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// --only-associated-rois used to be silent in both of its failure modes: without
    /// --associations it exported every ROI (there was nothing to narrow to), and with
    /// associations that matched nothing it exported no mask at all, both at exit 0.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class OnlyAssociatedRoisTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _cohort;

        public OnlyAssociatedRoisTests()
        {
            _dir = DicomTestData.NewTempDir();
            _cohort = Path.Combine(_dir, "cohort");

            string study = DicomTestData.NewUid();
            string frame = DicomTestData.NewUid();
            string ct = DicomTestData.NewUid();
            DicomTestData.WriteCtSeriesWithPixels(_cohort, ct, frame, sliceCount: 4, size: 16, studyUid: study);
            DicomTestData.WriteRtStructWithContours(_cohort, "rs.dcm", study, DicomTestData.NewUid(), frame,
                new List<KeyValuePair<string, double[]>>
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                    new KeyValuePair<string, double[]>("Avoid", DicomTestData.SquareContour(2, 6, 2)),
                },
                referencedSeriesUid: ct);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string WriteAssociations(string name, params (string canonical, string[] aliases)[] rows)
        {
            string path = Path.Combine(_dir, name);
            var json = "[" + string.Join(",", rows.Select(r =>
                "{\"CanonicalName\":\"" + r.canonical + "\",\"Aliases\":[" +
                string.Join(",", r.aliases.Select(a => "\"" + a + "\"")) + "]}")) + "]";
            File.WriteAllText(path, json);
            return path;
        }

        private CliRun Convert(params string[] extra)
        {
            var args = new List<string>
            {
                "--cohort-convert", "--input", _cohort,
                "--output", Path.Combine(_dir, "out_" + Guid.NewGuid().ToString("N").Substring(0, 6)),
                "--no-images", "--no-doses",
            };
            args.AddRange(extra);
            return CliRun.Execute(args.ToArray());
        }

        [Fact]
        public void WithoutAssociations_WarnsAndExportsEveryRoi()
        {
            var run = Convert("--only-associated-rois");
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains(CohortExportService.OnlyAssociatedWithoutAssociationsWarning, run.Stderr);
            Assert.Contains("Avoid.nii.gz", run.Stdout);
            Assert.Contains("Target.nii.gz", run.Stdout);
        }

        [Fact]
        public void AssociationsMatchNothing_WarnsAndExits1_AfterWritingTheManifestAndJson()
        {
            string assoc = WriteAssociations("liver.json", ("Liver", new[] { "Hepar" }));
            var run = Convert("--only-associated-rois", "--associations", assoc);

            Assert.True(run.ExitCode == 1, run.ToString());
            Assert.Contains("WARNING: --only-associated-rois matched no ROI", run.Stderr);
            Assert.Contains("[Liver]", run.Stderr);
            Assert.Contains("Avoid, Target", run.Stderr);
            Assert.DoesNotContain("FATAL", run.Stderr);
            Assert.DoesNotContain(".nii.gz", run.Stdout);

            // The cohort JSON is still emitted, and its errors carry the explanation.
            Assert.Contains("\"errors\"", run.Stdout);
            Assert.Contains("matched no ROI", run.Stdout);

            // The survey that shows which names exist was still written before the run failed.
            Assert.NotEmpty(Directory.GetFiles(_dir, "export_manifest.csv", SearchOption.AllDirectories));
        }

        [Fact]
        public void EveryStructuredSeriesFailed_IsNotBlamedOnTheAssociations()
        {
            // Slices without pixel data: the scan links them, the conversion cannot load them.
            string cohort = Path.Combine(_dir, "broken");
            string study = DicomTestData.NewUid();
            string frame = DicomTestData.NewUid();
            string ct = DicomTestData.NewUid();
            Directory.CreateDirectory(cohort);
            for (int i = 0; i < 3; i++)
                DicomTestData.WriteImageSlice(cohort, $"ct_{i}.dcm", "CT", "PAT9", study, ct, frame, i);
            DicomTestData.WriteRtStruct(cohort, "rs.dcm", "PAT9", study, DicomTestData.NewUid(), frame, "S",
                new[] { "Target" }, referencedSeriesUid: ct);
            string assoc = WriteAssociations("liver2.json", ("Liver", new[] { "Hepar" }));

            var run = CliRun.Execute("--cohort-convert", "--input", cohort, "--output", Path.Combine(_dir, "out_broken"),
                "--no-images", "--no-doses", "--only-associated-rois", "--associations", assoc);

            Assert.True(run.ExitCode == 1, run.ToString());
            Assert.DoesNotContain("matched no ROI", run.Stderr);
            Assert.DoesNotContain("matched no ROI", run.Stdout);
        }

        [Fact]
        public void AssociationsMatchSomething_ExportsOnlyThose_UnderCanonicalNames()
        {
            string assoc = WriteAssociations("target.json", ("GTV", new[] { "Target" }));
            var run = Convert("--only-associated-rois", "--associations", assoc);

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.DoesNotContain("WARNING: --only-associated-rois", run.Stderr);
            Assert.Contains("GTV.nii.gz", run.Stdout);
            Assert.DoesNotContain("Avoid.nii.gz", run.Stdout);
            Assert.DoesNotContain("Target.nii.gz", run.Stdout);
        }

        [Fact]
        public void ManifestOnly_ColumnsFollowTheSameFilter()
        {
            string assoc = WriteAssociations("target.json", ("GTV", new[] { "Target" }));
            string outRoot = Path.Combine(_dir, "manifest_only");
            var run = CliRun.Execute("--cohort-manifest", "--input", _cohort, "--output", outRoot,
                "--no-volumes", "--only-associated-rois", "--associations", assoc);

            Assert.True(run.ExitCode == 0, run.ToString());
            string header = File.ReadLines(Path.Combine(outRoot, "export_manifest.csv")).First();
            Assert.Contains("GTV", header);
            Assert.DoesNotContain("Avoid", header);
            Assert.DoesNotContain("Target", header);
        }
    }
}
