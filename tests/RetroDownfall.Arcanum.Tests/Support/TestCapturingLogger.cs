using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class TestCapturingLogger<TCategory> : ILogger<TCategory>
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    public IReadOnlyCollection<TestLogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue(new TestLogEntry(
            logLevel,
            formatter(state, exception),
            exception));
}

internal sealed record TestLogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception);
