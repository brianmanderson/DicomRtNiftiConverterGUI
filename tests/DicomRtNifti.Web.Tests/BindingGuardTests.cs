using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// D-33: the server listens on loopback only while no authentication is enforced, and never
    /// on a non-loopback address over plain HTTP. Checked three ways: the rules, the in-memory
    /// host (configuration through every route Kestrel reads), and real Kestrel through
    /// <see cref="Program.Main"/>, where a refused address must never be opened.
    /// </summary>
    public sealed class BindingGuardTests
    {
        [Theory]
        [InlineData("http://localhost:5180")]
        [InlineData("https://LOCALHOST:5001")]
        [InlineData("http://127.0.0.1:5180")]
        [InlineData("http://127.0.0.1:0")]
        [InlineData("http://127.0.0.2:80")]
        [InlineData("http://127.255.255.254:1")]
        [InlineData("http://[::1]:5180")]
        public void Loopback_addresses_are_loopback(string url) => Assert.True(BindingGuard.IsLoopback(url));

        [Theory]
        [InlineData("http://0.0.0.0:5180")]
        [InlineData("http://*:5180")]
        [InlineData("http://+:5180")]
        [InlineData("http://[::]:5180")]
        [InlineData("http://192.168.1.20:5180")]
        [InlineData("http://10.0.0.1:80")]
        [InlineData("http://myserver:5180")]
        [InlineData("http://localhost.example.com:5180")]
        [InlineData("http://127.0.0.1.nip.io:5180")]
        [InlineData("http://unix:/tmp/dicomrtnifti.sock")]
        [InlineData("http://pipe:/dicomrtnifti")]
        [InlineData("ftp://127.0.0.1:21")]
        [InlineData("not a url")]
        [InlineData("")]
        public void Everything_else_is_not(string url) => Assert.False(BindingGuard.IsLoopback(url));

        [Fact]
        public void Without_authentication_only_loopback_is_accepted()
        {
            Assert.Empty(BindingGuard.Refusals(new[] { "http://localhost:5180", "http://[::1]:5181", "http://127.0.0.1:0" }, false));

            var refusals = BindingGuard.Refusals(new[] { "http://127.0.0.1:5180", "http://0.0.0.0:5180", "https://*:443" }, false);

            Assert.Equal(2, refusals.Count);
            Assert.All(refusals, r => Assert.Contains("no authentication is configured", r));
        }

        [Fact]
        public void With_authentication_a_non_loopback_address_still_needs_https()
        {
            Assert.Empty(BindingGuard.Refusals(new[] { "https://0.0.0.0:443", "http://localhost:5180" }, true));

            var refusals = BindingGuard.Refusals(new[] { "http://0.0.0.0:80" }, true);

            Assert.Equal("http://0.0.0.0:80 is not a loopback address and is not HTTPS.", Assert.Single(refusals));
        }

        [Fact]
        public void Every_configuration_route_to_an_address_is_read()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["urls"] = "http://localhost:5000;http://0.0.0.0:5001",
                ["http_ports"] = "8080",
                ["https_ports"] = "8443",
                ["Kestrel:Endpoints:Public:Url"] = "http://0.0.0.0:5002",
                ["Kestrel:Endpoints:Odd:Protocols"] = "Http1",
            }).Build();

            var urls = BindingGuard.ConfiguredUrls(config);

            Assert.Equal(
                new[] { "http://localhost:5000", "http://0.0.0.0:5001", "http://*:8080", "https://*:8443", "(Kestrel:Endpoints:Odd has no Url)", "http://0.0.0.0:5002" }
                    .OrderBy(u => u),
                urls.OrderBy(u => u));
            // An endpoint whose address cannot be read is refused, not assumed safe.
            Assert.Contains(BindingGuard.Refusals(urls, false), r => r.StartsWith("(Kestrel:Endpoints:Odd", StringComparison.Ordinal));
        }

        [Fact]
        public void With_nothing_configured_the_kestrel_default_is_checked()
        {
            var urls = BindingGuard.ConfiguredUrls(new ConfigurationBuilder().Build());

            Assert.Equal(new[] { BindingGuard.KestrelDefaultUrl }, urls);
            Assert.Empty(BindingGuard.Refusals(urls, false));
        }

        [Fact]
        public async Task Authentication_counts_only_when_a_scheme_and_a_deny_anonymous_fallback_exist()
        {
            Assert.False(await BindingGuard.IsAuthenticationEnforcedAsync(new ServiceCollection().BuildServiceProvider()));

            var schemeOnly = new ServiceCollection().AddLogging();
            schemeOnly.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NoAuthHandler>("Test", null);
            schemeOnly.AddAuthorization();
            Assert.False(await BindingGuard.IsAuthenticationEnforcedAsync(schemeOnly.BuildServiceProvider()));

            var policyOnly = new ServiceCollection().AddLogging();
            policyOnly.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            Assert.False(await BindingGuard.IsAuthenticationEnforcedAsync(policyOnly.BuildServiceProvider()));

            var both = new ServiceCollection().AddLogging();
            both.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NoAuthHandler>("Test", null);
            both.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            Assert.True(await BindingGuard.IsAuthenticationEnforcedAsync(both.BuildServiceProvider()));
        }

        [Fact]
        public async Task The_app_as_shipped_enforces_no_authentication()
        {
            using var factory = new WebAppFactory();
            Assert.False(await BindingGuard.IsAuthenticationEnforcedAsync(factory.Services));
        }

        /// <summary>
        /// Each configuration route to a non-loopback address, on the real host with real Kestrel:
        /// the guard refuses before the server opens anything, so no port is ever bound. (The
        /// in-memory factory cannot show this: Main turns the refusal into exit code 2, which the
        /// factory reports only as a disposed host.)
        /// </summary>
        [Theory]
        [InlineData("--urls", "http://0.0.0.0:{port}")]
        [InlineData("--urls", "http://localhost:5180;http://*:{port}")]
        [InlineData("--urls", "http://[::]:{port}")]
        [InlineData("--urls", "http://myserver:{port}")]
        [InlineData("--http_ports", "{port}")]
        [InlineData("--https_ports", "{port}")]
        [InlineData("--Kestrel:Endpoints:Public:Url", "http://0.0.0.0:{port}")]
        public async Task The_host_refuses_to_start_on_a_non_loopback_address(string key, string value)
        {
            int port = FreePort();
            await using var app = Program.BuildApp(new[] { key, value.Replace("{port}", port.ToString()), "--environment", "Production" });

            var thrown = await Record.ExceptionAsync(() => app.StartAsync());

            var refused = WebAppFactory.FindInChain<BindingRefusedException>(thrown);
            Assert.True(refused != null, thrown?.ToString() ?? "the host started");
            Assert.NotEmpty(refused.Refusals);
            Assert.Contains("no authentication is configured", refused.Message);
            Assert.False(Accepts(IPAddress.Loopback, port), $"something is listening on port {port}");
        }

        [Fact]
        public async Task The_host_starts_on_loopback()
        {
            using var factory = new WebAppFactory(new Dictionary<string, string> { ["urls"] = "http://127.0.0.1:5180;http://[::1]:5180" });

            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }

        [Fact]
        public void Main_refuses_0_0_0_0_with_exit_code_2_and_never_opens_the_port()
        {
            int port = FreePort();

            int exit = Program.Main(new[] { "--urls", $"http://0.0.0.0:{port}", "--environment", "Production" });

            Assert.Equal(Program.RefusedExitCode, exit);
            Assert.False(Accepts(IPAddress.Loopback, port), $"something is listening on port {port}");
        }

        [Fact]
        public void Main_refuses_an_invalid_configuration_with_exit_code_2()
        {
            int exit = Program.Main(new[] { "--urls", "http://127.0.0.1:0", "--Web:DataRoots:0:Id", "Not An Id", "--Web:DataRoots:0:Path", "relative/path" });

            Assert.Equal(Program.RefusedExitCode, exit);
        }

        [Fact]
        public async Task Real_kestrel_on_loopback_serves_health()
        {
            await using var app = Program.BuildApp(new[] { "--urls", "http://127.0.0.1:0", "--environment", "Production" });
            await app.StartAsync();
            try
            {
                string address = app.Urls.Single();
                Assert.True(BindingGuard.IsLoopback(address), address);

                using var client = new HttpClient { BaseAddress = new Uri(address) };
                var response = await client.GetAsync("/health");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("SimpleITK native: OK", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                await app.StopAsync();
            }
        }

        /// <summary>
        /// The backstop: an address added past the configuration (here straight onto the server's
        /// address list) is caught once the server has bound it, and the host stops. It binds
        /// 0.0.0.0 for a moment, which raises a firewall prompt on a Windows desktop, so it runs
        /// in CI only (or with DICOMRTNIFTI_WEB_BIND_ANY=1).
        /// </summary>
        [CiFact]
        public async Task An_address_bound_past_the_configuration_stops_the_host()
        {
            await using var app = Program.BuildApp(new[] { "--urls", "http://127.0.0.1:0", "--environment", "Production" });
            app.Urls.Clear();
            app.Urls.Add("http://0.0.0.0:0");

            var thrown = await Record.ExceptionAsync(() => app.StartAsync());

            Assert.NotNull(WebAppFactory.FindInChain<BindingRefusedException>(thrown));
            await app.StopAsync();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static bool Accepts(IPAddress address, int port)
        {
            using var client = new TcpClient();
            try
            {
                client.Connect(address, port);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private sealed class NoAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public NoAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    /// <summary>A fact that runs in CI (GITHUB_ACTIONS=true) or when DICOMRTNIFTI_WEB_BIND_ANY=1.</summary>
    public sealed class CiFactAttribute : FactAttribute
    {
        public CiFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                && Environment.GetEnvironmentVariable("DICOMRTNIFTI_WEB_BIND_ANY") != "1")
                Skip = "binds 0.0.0.0; runs in CI, or set DICOMRTNIFTI_WEB_BIND_ANY=1";
        }
    }
}
