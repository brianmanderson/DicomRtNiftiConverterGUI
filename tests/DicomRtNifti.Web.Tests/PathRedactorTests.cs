using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// No absolute path outside a root reaches a response or a log (plan sections 3.3 and 6).
    /// The roots are written in this OS's form; the outside paths come in every form, since a
    /// message may quote a path from anywhere.
    /// </summary>
    public sealed class PathRedactorTests
    {
        private static readonly bool Windows = OperatingSystem.IsWindows();
        private static readonly string Archive = Windows ? @"C:\data\archive" : "/data/archive";
        private static readonly string Predicted = Windows ? @"C:\data\archive\predicted" : "/data/archive/predicted";
        private static readonly string Exports = Windows ? @"D:\exports" : "/exports";
        private static readonly string Keys = Windows ? @"D:\keys" : "/keys";
        private static readonly char Sep = Path.DirectorySeparatorChar;

        private static readonly PathRedactor Redactor = new PathRedactor(DataRoots.From(new WebOptions
        {
            DataRoots =
            {
                new DataRootOptions { Id = "archive", Path = Archive },
                new DataRootOptions { Id = "predicted", Path = Predicted, Access = RootAccess.ReadWrite },
            },
            OutputRoot = Exports,
            KeyRoot = Keys,
        }));

        public static IEnumerable<object[]> Cases()
        {
            // Configured folders become their id.
            yield return Case($"{Archive}{Sep}2024{Sep}ct.dcm", $"archive:2024{Sep}ct.dcm");
            yield return Case($"Scanning {Archive} ...", "Scanning archive: ...");
            yield return Case($"{Predicted}{Sep}case1{Sep}masks", $"predicted:case1{Sep}masks");
            yield return Case($"{Exports}{Sep}proj{Sep}export_manifest.csv", $"output:proj{Sep}export_manifest.csv");
            yield return Case($"The key {Keys}{Sep}proj{Sep}AnonymizationKey.json is outside the export.",
                $"The key keys:proj{Sep}AnonymizationKey.json is outside the export.");
            yield return Case($"'{Archive}{Sep}H&N 2024{Sep}Smith J{Sep}1.dcm' is unreadable",
                $"'archive:H&N 2024{Sep}Smith J{Sep}1.dcm' is unreadable");

            // A folder that merely shares a root's name as a prefix is not that root.
            yield return Case($"{Archive}2{Sep}x.dcm failed", $"{PathRedactor.OutsideRoots} failed");

            if (Windows)
            {
                yield return Case(@"c:\DATA\Archive\x.dcm", "archive:x.dcm");
                yield return Case("C:/data/archive/2024/x.dcm", "archive:2024/x.dcm");
                yield return Case(@"\\?\C:\data\archive\deep\x.dcm", @"archive:deep\x.dcm");
            }
            else
            {
                yield return Case("/DATA/archive/x.dcm", PathRedactor.OutsideRoots);
            }

            // Every other absolute path, whatever the OS that wrote it.
            yield return Case(@"C:\Users\someone\AppData\Local\x.dll", PathRedactor.OutsideRoots);
            yield return Case(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.13\x.dll", PathRedactor.OutsideRoots);
            yield return Case(@"\\fileserver\share\Patients\Smith John\ct.dcm", PathRedactor.OutsideRoots);
            yield return Case(@"\\?\C:\temp\long\x.dcm", PathRedactor.OutsideRoots);
            yield return Case(@"\\?\UNC\fileserver\share\x.dcm", PathRedactor.OutsideRoots);
            yield return Case(@"\\.\pipe\dicomrtnifti", PathRedactor.OutsideRoots);
            yield return Case("/home/runner/work/x/libSimpleITKCSharpNative.so", PathRedactor.OutsideRoots);
            yield return Case("/srv/Smith John/ct/x.dcm failed", $"{PathRedactor.OutsideRoots} failed");
            yield return Case(@"Unable to load 'C:\app\SimpleITKCSharpNative.dll': not found.",
                $"Unable to load '{PathRedactor.OutsideRoots}': not found.");
            yield return Case("dlopen(/usr/local/lib/libSimpleITKCSharpNative.dylib, 0x0001): image not found",
                $"dlopen({PathRedactor.OutsideRoots}, 0x0001): image not found");
            yield return Case("--input=/srv/dicom/2024 --json-out /tmp/scan.json",
                $"--input={PathRedactor.OutsideRoots} --json-out {PathRedactor.OutsideRoots}");
            yield return Case(@"Copied C:\a\b.txt to D:\c\d.txt.",
                $"Copied {PathRedactor.OutsideRoots} to {PathRedactor.OutsideRoots}.");
            yield return Case($"{Archive}{Sep}ok.dcm and /etc/passwd",
                $"archive:ok.dcm and {PathRedactor.OutsideRoots}");

            // Not paths: left alone.
            yield return Case("Scanned 3/10 files...", "Scanned 3/10 files...");
            yield return Case("http://127.0.0.1:5180/health", "http://127.0.0.1:5180/health");
            yield return Case("and/or", "and/or");
            yield return Case("schema dicomrtnifti.cohort-plan/1", "schema dicomrtnifti.cohort-plan/1");
            yield return Case("archive:2024/H&N", "archive:2024/H&N");
            yield return Case("2026/11/03 10:00", "2026/11/03 10:00");
            yield return Case("SimpleITK native: OK (1-voxel probe)", "SimpleITK native: OK (1-voxel probe)");
            yield return Case("", "");
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Redacts(string input, string expected)
        {
            string redacted = Redactor.Redact(input);

            Assert.Equal(expected, redacted);
            Assert.Equal(redacted, Redactor.Redact(redacted));
        }

        [Fact]
        public void Null_is_empty()
        {
            Assert.Equal("", Redactor.Redact(null));
        }

        [Fact]
        public void A_nested_root_wins_over_the_root_that_contains_it()
        {
            Assert.StartsWith("predicted:", Redactor.Redact($"{Predicted}{Sep}a"));
            Assert.StartsWith("archive:", Redactor.Redact($"{Archive}{Sep}a"));
        }

        [Fact]
        public void With_no_roots_every_absolute_path_is_redacted()
        {
            Assert.Equal($"read {PathRedactor.OutsideRoots}", PathRedactor.NoRoots.Redact($"read {Archive}{Sep}x.dcm"));
        }

        [Fact]
        public void Every_log_line_is_redacted_before_a_provider_sees_it()
        {
            var capture = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton(Redactor);
            RedactingLoggerFactory.Install(services);
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("test");

            logger.LogInformation("Reading {Path} for {Root}", "/home/someone/secret/x.dcm", "archive");
            logger.LogError(new IOException(@"Access to C:\Users\someone\x.dcm is denied."), "Scan failed under {Path}", $"{Archive}{Sep}2024");

            var info = capture.Logs.Single(l => l.Level == LogLevel.Information);
            Assert.Equal($"Reading {PathRedactor.OutsideRoots} for archive", info.Message);
            // Structured providers see only the redacted message, not the original arguments.
            Assert.Equal(info.Message, Assert.Single(info.State).Value);

            var error = capture.Logs.Single(l => l.Level == LogLevel.Error);
            Assert.Null(error.Exception);
            Assert.StartsWith($"Scan failed under archive:2024{Environment.NewLine}System.IO.IOException: Access to {PathRedactor.OutsideRoots} is denied.", error.Message);
            Assert.DoesNotContain("someone", error.Message);
        }

        [Fact]
        public async Task The_host_logs_no_server_path()
        {
            var capture = new CapturingLoggerProvider();
            using (var factory = new WebAppFactory(logProvider: capture))
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/");
                await client.GetAsync("/no-such-page");
            }

            Assert.NotEmpty(capture.Logs);
            string repo = RepoPaths.Root;
            Assert.All(capture.Logs, l => Assert.DoesNotContain(repo, l.Message, StringComparison.OrdinalIgnoreCase));
            Assert.All(capture.Logs, l => Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Sep), l.Message, StringComparison.OrdinalIgnoreCase));
        }

        private static object[] Case(string input, string expected) => new object[] { input, expected };
    }
}
