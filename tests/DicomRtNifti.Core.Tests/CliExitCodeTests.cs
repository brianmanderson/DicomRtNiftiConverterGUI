using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The CLI's exit-code contract (CLAUDE.md, README): 0 ok, 2 when the command line cannot be
    /// acted on, 1 when the conversion itself fails. Before W1-C only "no arguments" and "unknown
    /// mode" returned 2; a missing flag or a missing input file fell through the blanket catch to 1
    /// with a stack trace, so a script could not tell a typo from a broken conversion.
    /// </summary>
    [Collection(CliConsoleCollection.Name)]
    public class CliExitCodeTests : IDisposable
    {
        private readonly string _dir;

        public CliExitCodeTests()
        {
            _dir = DicomTestData.NewTempDir();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void NoArguments_Exits2WithUsage()
        {
            var run = CliRun.Execute();
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("--forward", run.Stderr);
        }

        [Fact]
        public void UnknownMode_Exits2()
        {
            var run = CliRun.Execute("--frobnicate");
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("requires --forward", run.Stderr);
        }

        [Fact]
        public void Help_Exits0()
        {
            Assert.Equal(0, CliRun.Execute("--help").ExitCode);
        }

        [Fact]
        public void Forward_MissingRequiredFlag_Exits2_NamingTheFlag()
        {
            var run = CliRun.Execute("--forward", "--image-folder", _dir, "--output-folder", _dir);
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("ERROR: Required argument '--rtstruct' is missing.", run.Stderr);
            Assert.DoesNotContain("FATAL", run.Stderr);
            Assert.DoesNotContain("   at ", run.Stderr);
        }

        [Fact]
        public void Forward_RtstructPathDoesNotExist_Exits2()
        {
            string missing = Path.Combine(_dir, "nope.dcm");
            var run = CliRun.Execute("--forward", "--rtstruct", missing,
                "--image-folder", _dir, "--output-folder", Path.Combine(_dir, "out"));
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("RTSTRUCT not found", run.Stderr);
        }

        [Fact]
        public void Forward_ImageFolderDoesNotExist_Exits2()
        {
            string rtstruct = Path.Combine(_dir, "rs.dcm");
            File.WriteAllText(rtstruct, "placeholder: existence is all this test needs");
            var run = CliRun.Execute("--forward", "--rtstruct", rtstruct,
                "--image-folder", Path.Combine(_dir, "no-such-folder"), "--output-folder", _dir);
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("Image folder not found", run.Stderr);
        }

        [Fact]
        public void Forward_InputsExistButRtstructIsNotDicom_Exits1()
        {
            // The command line was fine; the conversion is what failed.
            string rtstruct = Path.Combine(_dir, "rs.dcm");
            File.WriteAllText(rtstruct, "this is not a DICOM file");
            string images = Path.Combine(_dir, "ct");
            DicomTestData.WriteCtSeriesWithPixels(images, DicomTestData.NewUid(), DicomTestData.NewUid(), sliceCount: 2, size: 8);

            var run = CliRun.Execute("--forward", "--rtstruct", rtstruct,
                "--image-folder", images, "--output-folder", Path.Combine(_dir, "out"));
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("FATAL", run.Stderr);
        }

        [Fact]
        public void CohortConvert_MalformedSpacing_Exits2()
        {
            var run = CliRun.Execute("--cohort-convert", "--input", _dir, "--output", Path.Combine(_dir, "out"),
                "--output-spacing", "1,x,3");
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("--output-spacing", run.Stderr);
        }

        [Fact]
        public void CohortConvert_AssociationsFileMissing_Exits2()
        {
            var run = CliRun.Execute("--cohort-convert", "--input", _dir, "--output", Path.Combine(_dir, "out"),
                "--associations", Path.Combine(_dir, "missing.json"));
            Assert.Equal(2, run.ExitCode);
            Assert.Contains("Associations file not found", run.Stderr);
        }
    }
}
