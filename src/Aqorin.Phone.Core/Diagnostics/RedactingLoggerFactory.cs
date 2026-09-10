using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Core.Diagnostics;

/// <summary>
/// Wraps an <see cref="ILoggerFactory"/> so every formatted message is passed through
/// <see cref="SensitiveDataRedactor"/> before reaching the real providers. Handed to third-party libraries
/// (the SIP stack) whose internal logging we do not control.
/// </summary>
public sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
        // The inner factory is owned by the DI container.
    }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            var message = SensitiveDataRedactor.Redact(formatter(state, exception));
            inner.Log(logLevel, eventId, message, exception, static (s, _) => s);
        }
    }
}
