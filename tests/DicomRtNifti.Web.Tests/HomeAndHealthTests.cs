using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Bunit;
using DicomRtNifti.Web.Features.Home;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The Home page and /health report the version and Core's SimpleITK native probe, and the
    /// Home page lists every configured folder by id with its availability, never by path. Every
    /// response carries no-store and the security headers.
    /// </summary>
    public sealed class HomeAndHealthTests : IClassFixture<TraversalTree>
    {
        private static readonly string Version =
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        private readonly TraversalTree _tree;

        public HomeAndHealthTests(TraversalTree tree)
        {
            _tree = tree;
        }

        [Fact]
        public void Home_shows_version_probe_and_folders_by_id_only()
        {
            using var context = ComponentHarness.Create(new WebOptions
            {
                DataRoots =
                {
                    new DataRootOptions { Id = "archive", Path = _tree.Root },
                    new DataRootOptions { Id = "gone", Path = Path.Combine(_tree.Base, "gone"), Access = RootAccess.ReadWrite },
                },
                OutputRoot = _tree.Outside,
                StateRoot = _tree.State,
            });

            var home = context.Render<HomePage>();
            home.WaitForAssertion(() => Assert.DoesNotContain(home.FindAll(".status-pending"), c => c.TextContent == "checking"), TimeSpan.FromSeconds(10));

            Assert.Equal("DicomRtNifti web", home.Find("h1").TextContent);
            Assert.Contains(Version, home.Markup);
            Assert.Contains("SimpleITK native: OK", home.Find(".facts .status-ok").TextContent);
            Assert.Empty(home.FindAll(".notice-failed"));

            var rows = home.FindAll("table.roots").First().QuerySelectorAll("tbody tr")
                .Select(r => r.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray())
                .ToList();
            Assert.Equal(new[] { "archive", "gone", "output" }, rows.Select(r => r[0]));
            Assert.Equal(new[] { "read-only", "read-write", "read-write" }, rows.Select(r => r[1]));
            Assert.Equal(new[] { "available", "missing", "available" }, rows.Select(r => r[2]));
            Assert.EndsWith("GiB", rows[0][3]);

            var folders = home.FindAll("table.roots").Last().QuerySelectorAll("tbody tr")
                .Select(r => r.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray())
                .ToList();
            Assert.Equal(new[] { "Keys", "Uploads", "State" }, folders.Select(r => r[0]));
            Assert.Equal(new[] { "not configured", "not configured", "available" }, folders.Select(r => r[2]));

            Assert.Equal("DicomRtNifti.Cli --version", home.Find(".cli pre code").TextContent);
            Assert.DoesNotContain(_tree.Base, home.Markup, StringComparison.OrdinalIgnoreCase);

            SaveForThePullRequest(home.Markup);
        }

        [Fact]
        public void Home_without_roots_says_how_to_configure_them()
        {
            using var context = ComponentHarness.Create();

            var home = context.Render<HomePage>();

            Assert.Contains("No data roots are configured.", home.Markup);
            Assert.Equal(3, home.FindAll("table.roots tbody tr").Count);
        }

        [Fact]
        public async Task Health_reports_the_probe()
        {
            using var factory = new WebAppFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync(HealthEndpoint.Route);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal(Version, root.GetProperty("version").GetString());
            Assert.True(root.GetProperty("simpleitk_native").GetProperty("ok").GetBoolean());
            Assert.StartsWith("SimpleITK native: OK", root.GetProperty("simpleitk_native").GetProperty("probe").GetString());
        }

        [Fact]
        public void The_version_is_traceable_to_a_commit()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{40}$", Version);
            var status = new HostStatus(PathRedactor.NoRoots);
            Assert.Equal(Version.Substring(0, Version.IndexOf('+') + 8), status.DisplayVersion);
        }

        [Theory]
        [InlineData("/", HttpStatusCode.OK)]
        [InlineData("/inventory", HttpStatusCode.OK)]
        [InlineData("/health", HttpStatusCode.OK)]
        [InlineData("/app.css", HttpStatusCode.OK)]
        [InlineData("/_framework/blazor.web.js", HttpStatusCode.OK)]
        [InlineData("/no-such-page", HttpStatusCode.NotFound)]
        public async Task Every_response_carries_no_store_and_the_security_headers(string path, HttpStatusCode status)
        {
            using var factory = new WebAppFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync(path);

            Assert.Equal(status, response.StatusCode);
            foreach (var (name, value) in SecurityHeaders.Values)
                Assert.Equal(value, string.Join(", ", HeaderValues(response, name)));
            Assert.Empty(HeaderValues(response, "Server"));
            Assert.Empty(HeaderValues(response, "Expires"));
        }

        [Fact]
        public async Task A_request_for_another_host_name_is_refused()
        {
            // DNS rebinding: a page on another site that resolves its own name to 127.0.0.1.
            using var factory = new WebAppFactory();
            using var client = factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Host = "attacker.example";

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        private static IEnumerable<string> HeaderValues(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
                return values;
            if (response.Content.Headers.TryGetValues(name, out values))
                return values;
            return Array.Empty<string>();
        }

        /// <summary>
        /// The rendered Home page, for the pull request and as a CI artifact, written when
        /// DICOMRTNIFTI_WEB_RENDER_DIR names a folder.
        /// </summary>
        private static void SaveForThePullRequest(string markup)
        {
            string dir = Environment.GetEnvironmentVariable("DICOMRTNIFTI_WEB_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
                return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "home.bunit.html"), markup);
        }
    }
}
