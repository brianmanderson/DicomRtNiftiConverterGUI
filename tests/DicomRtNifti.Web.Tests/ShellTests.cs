using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bunit;
using DicomRtNifti.Web.Features.Home;
using DicomRtNifti.Web.Shell;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// The shell: features are discovered, each has a route and a menu entry, nothing routable
    /// hides outside the menu, no route carries an identifier, and every placeholder page says
    /// it is not available yet and shows its CLI equivalent.
    /// </summary>
    public sealed class ShellTests
    {
        private static readonly FeatureCatalog Catalog = FeatureCatalog.Discover(typeof(Program).Assembly);

        /// <summary>Plan section 4, in menu order.</summary>
        private static readonly string[] PlanPages =
        {
            "Home",
            "Data sources", "Inventory", "Analysis", "ROI curation", "Export setup", "Plan review", "Jobs", "Results",
            "Reverse", "Upload", "Watch folders",
            "Settings", "Help", "Audit",
        };

        /// <summary>Routable components that are not features, and why.</summary>
        private static readonly Type[] NotInTheMenu = { typeof(Error), typeof(NotFound) };

        private static readonly string[] IdentifierWords =
            { "patient", "mrn", "uid", "series", "study", "accession", "name", "birth", "path", "folder", "file" };

        public static IEnumerable<object[]> Placeholders() =>
            Catalog.All.Where(d => d.Page != typeof(HomePage)).Select(d => new object[] { d.Title });

        [Fact]
        public void Every_page_of_the_plan_is_a_feature_in_menu_order()
        {
            Assert.Equal(PlanPages, Catalog.All.Select(d => d.Title));
            Assert.Equal("/", Catalog.All[0].Route);
        }

        [Fact]
        public void Each_feature_route_is_its_pages_single_route()
        {
            foreach (var feature in Catalog.All)
            {
                var route = Assert.Single(feature.Page.GetCustomAttributes<RouteAttribute>());
                Assert.Equal(route.Template, feature.Route);
            }

            Assert.Equal(Catalog.All.Count, Catalog.All.Select(d => d.Route.ToLowerInvariant()).Distinct().Count());
        }

        [Fact]
        public void Nothing_routable_is_left_out_of_the_menu()
        {
            var routable = typeof(Program).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
                .ToList();

            var unlisted = routable.Except(Catalog.All.Select(d => d.Page)).Except(NotInTheMenu).ToList();

            Assert.True(unlisted.Count == 0, "Routable but not a feature: " + string.Join(", ", unlisted.Select(t => t.FullName)));
        }

        [Fact]
        public void No_route_carries_an_identifier()
        {
            var templates = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
                .Select(r => r.Template)
                .ToList();

            Assert.NotEmpty(templates);
            Assert.All(templates, t => Assert.False(NamesAnIdentifier(t), t));
            // The rule itself: an opaque id is allowed, a patient or a path is not.
            Assert.False(NamesAnIdentifier("/jobs/{jobId}"));
            Assert.True(NamesAnIdentifier("/inventory/{patientId}"));
            Assert.True(NamesAnIdentifier("/browse/{*folderPath}"));
            Assert.True(NamesAnIdentifier("/series/{SeriesInstanceUid:guid}"));
        }

        [Fact]
        public void The_menu_links_every_feature_under_its_section()
        {
            using var context = ComponentHarness.Create();

            var menu = context.Render<NavMenu>();

            var links = menu.FindAll("a");
            Assert.Equal(Catalog.All.Select(d => d.Title), links.Select(a => a.TextContent.Trim()));
            Assert.Equal(Catalog.All.Select(d => d.Href), links.Select(a => a.GetAttribute("href")));
            Assert.Equal(
                new[] { "Cohort", "Reverse and intake", "Server" },
                menu.FindAll(".nav-section").Select(s => s.TextContent.Trim()));
        }

        [Theory]
        [MemberData(nameof(Placeholders))]
        public void A_placeholder_says_it_is_not_available_and_shows_its_cli(string title)
        {
            var feature = Catalog.All.Single(d => d.Title == title);
            using var context = ComponentHarness.Create();

            var page = context.Render(builder =>
            {
                builder.OpenComponent(0, feature.Page);
                builder.CloseComponent();
            });

            Assert.Equal(title, page.Find("h1").TextContent);
            Assert.Contains("Not yet available in the web app.", page.Find(".notice-pending").TextContent);
            var cli = feature.Feature.Cli;
            if (cli.Command != null)
                Assert.Equal(cli.Command, page.Find(".cli pre code").TextContent);
            else
                Assert.Equal("No CLI command.", page.Find(".cli-none").TextContent);
            Assert.Equal(cli.Planned, page.FindAll(".cli-planned").Count == 1);
            Assert.False(string.IsNullOrWhiteSpace(feature.Feature.Summary));
        }

        [Fact]
        public async Task Every_page_is_served()
        {
            using var factory = new WebAppFactory();
            using var client = factory.CreateClient();

            foreach (var feature in Catalog.All)
            {
                var response = await client.GetAsync(feature.Route);
                string html = await response.Content.ReadAsStringAsync();

                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{feature.Route}: {(int)response.StatusCode}");
                Assert.Contains($"<h1>{(feature.Page == typeof(HomePage) ? "DicomRtNifti web" : feature.Title)}</h1>", html);
                Assert.Contains($"href=\"{feature.Href}\"", html);
            }
        }

        [Fact]
        public async Task An_unknown_address_is_the_not_found_page()
        {
            using var factory = new WebAppFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/no-such-page");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("There is no page at this address.", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public void Discovery_refuses_colliding_or_incomplete_features()
        {
            var ok = new FakeFeature("A", typeof(FakePageA));

            Assert.Equal(2, FeatureCatalog.From(new IWebFeature[] { ok, new FakeFeature("B", typeof(FakePageB)) }).All.Count);
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { ok, new FakeFeature("a", typeof(FakePageB)) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { ok, new FakeFeature("B", typeof(FakePageA)) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { new FakeFeature("C", typeof(FakePageNoRoute)) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { new FakeFeature("D", typeof(FakePageWithParameter)) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { new FakeFeature("E", typeof(string)) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { new FakeFeature("F", typeof(FakePageA), hasCli: false) }));
            Assert.Throws<InvalidOperationException>(() => FeatureCatalog.From(new IWebFeature[] { new FakeFeature(" ", typeof(FakePageA)) }));
        }

        private static bool NamesAnIdentifier(string template) =>
            Regex.Matches(template, @"\{\**([^}:=?]+)")
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Any(name => IdentifierWords.Any(name.Contains));

        private sealed class FakeFeature : IWebFeature
        {
            public FakeFeature(string title, Type page, bool hasCli = true)
            {
                Title = title;
                Page = page;
                Cli = hasCli ? new CliEquivalent(null, "none") : null;
            }

            public string Title { get; }
            public NavSection Section => NavSection.Server;
            public int Order => 0;
            public Type Page { get; }
            public string Summary => "fake";
            public CliEquivalent Cli { get; }
        }

        [Route("/fake-a")]
        private sealed class FakePageA : ComponentBase
        {
        }

        [Route("/fake-b")]
        private sealed class FakePageB : ComponentBase
        {
        }

        private sealed class FakePageNoRoute : ComponentBase
        {
        }

        [Route("/fake/{id}")]
        private sealed class FakePageWithParameter : ComponentBase
        {
        }
    }
}
