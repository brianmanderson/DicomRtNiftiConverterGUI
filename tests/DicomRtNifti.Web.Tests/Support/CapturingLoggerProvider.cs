using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>One log call as a provider received it.</summary>
    internal sealed class CapturedLog
    {
        public string Category { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public IReadOnlyList<KeyValuePair<string, object>> State { get; set; }
    }

    /// <summary>Records everything logged through it, as a console or JSON provider would see it.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _logs = new ConcurrentQueue<CapturedLog>();

        public IReadOnlyList<CapturedLog> Logs => _logs.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _logs);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<CapturedLog> _logs;

            public Logger(string category, ConcurrentQueue<CapturedLog> logs)
            {
                _category = category;
                _logs = logs;
            }

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _logs.Enqueue(new CapturedLog
                {
                    Category = _category,
                    Level = logLevel,
                    Message = formatter(state, exception),
                    Exception = exception,
                    State = (state as IEnumerable<KeyValuePair<string, object>>)?.ToList(),
                });
            }
        }
    }
}
