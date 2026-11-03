using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// The web app on an in-memory test server, in the Production environment (the exception
    /// handler and static-asset path a deployment uses), with extra configuration on top of
    /// appsettings.json and an optional extra log provider.
    /// </summary>
    internal sealed class WebAppFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string> _settings;
        private readonly ILoggerProvider _logProvider;

        public WebAppFactory(IDictionary<string, string> settings = null, ILoggerProvider logProvider = null)
        {
            _settings = new Dictionary<string, string>(settings ?? new Dictionary<string, string>());
            _logProvider = logProvider;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_settings));
            if (_logProvider != null)
                builder.ConfigureLogging(logging => logging.AddProvider(_logProvider));
        }

        /// <summary>Settings that make <paramref name="rootId"/> a read-only data root at <paramref name="path"/>.</summary>
        public static Dictionary<string, string> WithRoot(string rootId, string path, int index = 0) =>
            new Dictionary<string, string>
            {
                [$"Web:DataRoots:{index}:Id"] = rootId,
                [$"Web:DataRoots:{index}:Path"] = path,
            };

        /// <summary>The first <typeparamref name="T"/> in the chain of <paramref name="exception"/>, or null.</summary>
        public static T FindInChain<T>(Exception exception)
            where T : Exception
        {
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                if (ex is T match)
                    return match;
                if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        var found = FindInChain<T>(inner);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }
    }
}
