using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Core.Diagnostics;

public sealed record DiagnosticsEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Category}: {Message}";
}

/// <summary>
/// Bounded in-memory log sink used by the UI diagnostics panel. Every message passes through
/// <see cref="SensitiveDataRedactor"/> so credentials never reach the screen.
/// </summary>
public sealed class DiagnosticsLog
{
    private readonly ConcurrentQueue<DiagnosticsEntry> _entries = new();
    private readonly int _capacity;
    private int _count;

    public DiagnosticsLog(int capacity = 2000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Raised on the logging thread. Subscribers must marshal to the UI thread themselves.</summary>
    public event Action<DiagnosticsEntry>? EntryAdded;

    /// <summary>Minimum level kept. Diagnostic mode lowers this to Debug/Trace.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public void Add(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new DiagnosticsEntry(DateTimeOffset.Now, level, ShortCategory(category), SensitiveDataRedactor.Redact(message));
        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > _capacity && _entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<DiagnosticsEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }
}

/// <summary><see cref="ILoggerProvider"/> that forwards to a <see cref="DiagnosticsLog"/>.</summary>
public sealed class DiagnosticsLoggerProvider(DiagnosticsLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticsLogger(log, categoryName);

    public void Dispose()
    {
    }

    private sealed class DiagnosticsLogger(DiagnosticsLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= log.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += " | " + exception.GetType().Name + ": " + exception.Message;
            }

            log.Add(logLevel, category, message);
        }
    }
}
