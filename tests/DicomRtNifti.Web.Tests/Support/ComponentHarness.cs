using Bunit;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>A bUnit context with the services the shell and the pages inject, as Program registers them.</summary>
    internal static class ComponentHarness
    {
        public static BunitContext Create(WebOptions options = null)
        {
            var context = new BunitContext();
            var roots = DataRoots.From(options ?? new WebOptions());
            var redactor = new PathRedactor(roots);
            context.Services.AddSingleton(roots);
            context.Services.AddSingleton(redactor);
            context.Services.AddSingleton(new DataRootResolver(roots));
            context.Services.AddSingleton(new HostStatus(redactor));
            context.Services.AddSingleton(FeatureCatalog.Discover(typeof(Program).Assembly));
            return context;
        }
    }
}
