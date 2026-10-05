using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Collects every line a host's loggers write, for a test that asserts what the operator was told.
/// </summary>
internal sealed class CapturedLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturedLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturedLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

internal sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
