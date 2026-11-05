using System.Collections;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// Wraps the logger factory so every log line, from this app and from the framework, passes the
/// <see cref="PathRedactor"/> before any provider sees it: the console, the event log and a JSON
/// formatter alike receive only the redacted message, and an exception arrives as redacted text
/// in the message rather than as an object a provider would print verbatim. That is what keeps,
/// for example, the host's "Content root path:" line from writing a server path to the log.
/// Scopes pass through unchanged (they carry request ids and URLs, not file paths).
/// </summary>
public sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly Func<PathRedactor> _redactor;

    public RedactingLoggerFactory(ILoggerFactory inner, Func<PathRedactor> redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    /// <summary>Replaces the default <see cref="ILoggerFactory"/> registration with the redacting one.</summary>
    public static void Install(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp =>
        {
            var inner = new LoggerFactory(
                sp.GetServices<ILoggerProvider>(),
                sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
                sp.GetService<IOptions<LoggerFactoryOptions>>(),
                sp.GetService<IExternalScopeProvider>());
            PathRedactor? redactor = null;
            return new RedactingLoggerFactory(inner, () => redactor ??= ConfiguredOrNoRoots(sp));
        }));
    }

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName), _redactor);

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public void Dispose() => _inner.Dispose();

    // An invalid configuration fails options validation (or binding); the host still logs that
    // failure, and logging must neither throw nor need the configuration it could not read. With
    // no roots known, every absolute path is redacted.
    private static PathRedactor ConfiguredOrNoRoots(IServiceProvider services)
    {
        try
        {
            return services.GetRequiredService<PathRedactor>();
        }
        catch (Exception)
        {
            return PathRedactor.NoRoots;
        }
    }

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly Func<PathRedactor> _redactor;

        public RedactingLogger(ILogger inner, Func<PathRedactor> redactor)
        {
            _inner = inner;
            _redactor = redactor;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_inner.IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            if (exception is not null)
                message += Environment.NewLine + exception;
            _inner.Log(logLevel, eventId, new RedactedState(_redactor().Redact(message)), null, RedactedState.Format);
        }
    }

    /// <summary>A log state with one entry, the redacted message, so structured providers see nothing else.</summary>
    private sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Func<RedactedState, Exception?, string> Format = (state, _) => state.Message;

        public RedactedState(string message)
        {
            Message = message;
        }

        public string Message { get; }

        public int Count => 1;

        public KeyValuePair<string, object?> this[int index] =>
            index == 0 ? new KeyValuePair<string, object?>("{OriginalFormat}", Message) : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}
