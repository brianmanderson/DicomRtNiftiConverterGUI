using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The server refuses at startup a configuration its guards could not honour, and says why
    /// without printing a path. The shipped appsettings.json names no real folder (the repository
    /// is public, R-7).
    /// </summary>
    public sealed class WebOptionsTests
    {
        private static readonly bool Windows = OperatingSystem.IsWindows();
        private static readonly string A = Windows ? @"C:\data\archive" : "/data/archive";
        private static readonly string B = Windows ? @"C:\data\other" : "/data/other";
        private static readonly string Exports = Windows ? @"D:\exports" : "/exports";
        private static readonly string Keys = Windows ? @"D:\keys" : "/keys";
        private static readonly string State = Windows ? @"D:\state" : "/state";

        private static WebOptions Valid() => new WebOptions
        {
            DataRoots =
            {
                new DataRootOptions { Id = "archive", Path = A },
                new DataRootOptions { Id = "predicted-2", Path = B, Access = RootAccess.ReadWrite },
            },
            OutputRoot = Exports,
            KeyRoot = Keys,
            StateRoot = State,
        };

        private static string[] Failures(WebOptions options)
        {
            var result = new WebOptionsValidator().Validate(null, options);
            return result.Succeeded ? Array.Empty<string>() : result.Failures.ToArray();
        }

        [Fact]
        public void A_sound_configuration_passes()
        {
            Assert.Empty(Failures(Valid()));
            Assert.Empty(Failures(new WebOptions()));
        }

        [Theory]
        [InlineData("Archive")]
        [InlineData("1archive")]
        [InlineData("")]
        [InlineData("arch ive")]
        [InlineData("archive:")]
        [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
        public void A_root_id_must_be_a_short_lower_case_name(string id)
        {
            var options = Valid();
            options.DataRoots[0].Id = id;

            Assert.Contains(Failures(options), f => f.StartsWith("Web:DataRoots:0:Id must be", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("output")]
        [InlineData("keys")]
        [InlineData("uploads")]
        [InlineData("state")]
        public void A_root_id_may_not_take_a_server_folder_name(string id)
        {
            var options = Valid();
            options.DataRoots[0].Id = id;

            Assert.Contains(Failures(options), f => f.Contains("is reserved"));
        }

        [Fact]
        public void Root_ids_are_unique()
        {
            var options = Valid();
            options.DataRoots[1].Id = "archive";

            Assert.Contains(Failures(options), f => f.Contains("is used by another data root"));
        }

        [Theory]
        [InlineData("data/archive")]
        [InlineData("")]
        [InlineData(@"\\?\C:\data")]
        [InlineData(@"\\.\C:\data")]
        public void A_root_path_must_be_fully_qualified(string path)
        {
            var options = Valid();
            options.DataRoots[0].Path = path;

            var failures = Failures(options);

            Assert.Contains(failures, f => f.StartsWith("Web:DataRoots:0:Path must be", StringComparison.Ordinal));
        }

        [Fact]
        public void A_rooted_windows_path_without_a_drive_is_not_fully_qualified()
        {
            if (!Windows)
                return; // "/data" is fully qualified on POSIX
            var options = Valid();
            options.DataRoots[0].Path = @"\data\archive";

            Assert.Contains(Failures(options), f => f.StartsWith("Web:DataRoots:0:Path must be", StringComparison.Ordinal));
        }

        [Fact]
        public void The_key_and_state_roots_may_not_overlap_a_browsable_folder()
        {
            var inside = Valid();
            inside.KeyRoot = Path.Combine(A, "keys");
            inside.StateRoot = Exports;
            var failures = Failures(inside);
            Assert.Contains(failures, f => f.StartsWith("Web:KeyRoot overlaps data root 'archive'", StringComparison.Ordinal));
            Assert.Contains(failures, f => f.StartsWith("Web:StateRoot overlaps OutputRoot", StringComparison.Ordinal));

            var containing = Valid();
            containing.KeyRoot = Path.GetDirectoryName(A);
            Assert.Contains(Failures(containing), f => f.StartsWith("Web:KeyRoot overlaps data root 'archive'", StringComparison.Ordinal));

            var prefixOnly = Valid();
            prefixOnly.KeyRoot = A + "-keys";
            Assert.Empty(Failures(prefixOnly));
        }

        [Fact]
        public void Limits_and_lanes_must_be_positive()
        {
            var options = Valid();
            options.Limits.UploadBytes = 0;
            options.Limits.DownloadZipBytes = -1;
            options.Limits.UploadRetentionHours = 0;
            options.Lanes.MaxHeavyWorkers = 0;
            options.Lanes.MaxLightWorkers = 0;
            options.Lanes.QueueCapacity = 0;

            Assert.Equal(6, Failures(options).Length);
        }

        [Fact]
        public void No_failure_message_carries_a_path()
        {
            var options = Valid();
            options.DataRoots[0].Path = "relative/secret-folder";
            options.KeyRoot = Path.Combine(B, "keys-secret");
            options.UploadRoot = "uploads-secret";

            var failures = Failures(options);

            Assert.NotEmpty(failures);
            Assert.All(failures, f => Assert.DoesNotContain("secret", f));
        }

        [Fact]
        public async Task The_host_validates_at_startup()
        {
            await using var app = Program.BuildApp(new[]
            {
                "--urls", "http://127.0.0.1:0", "--environment", "Production",
                "--Web:DataRoots:0:Id", "Bad Id", "--Web:DataRoots:0:Path", A,
            });

            var thrown = await Record.ExceptionAsync(() => app.StartAsync());

            var invalid = WebAppFactory.FindInChain<OptionsValidationException>(thrown);
            Assert.True(invalid != null, thrown?.ToString() ?? "the host started");
            Assert.StartsWith("Refusing to start: the configuration is invalid.", Program.StartupRefusal(thrown));
            Assert.Contains("Web:DataRoots:0:Id must be", Program.StartupRefusal(thrown));
        }

        [Fact]
        public void The_shipped_appsettings_names_no_folder_and_binds_loopback()
        {
            using var json = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(RepoPaths.WebProject, "appsettings.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var root = json.RootElement;
            var web = root.GetProperty("Web");

            Assert.Equal(0, web.GetProperty("DataRoots").GetArrayLength());
            foreach (string folder in new[] { "OutputRoot", "KeyRoot", "UploadRoot", "StateRoot" })
                Assert.False(web.TryGetProperty(folder, out _), folder);
            Assert.True(BindingGuard.IsLoopback(root.GetProperty("Urls").GetString()));
        }
    }
}
