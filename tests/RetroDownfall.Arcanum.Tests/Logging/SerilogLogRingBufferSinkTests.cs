using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;
using Serilog.Events;
using Serilog.Parsing;

namespace RetroDownfall.Arcanum.Tests.Logging;

public sealed class SerilogLogRingBufferSinkTests
{

    /// <summary>
    /// A categorized single-placeholder log carries exactly SourceContext plus one real property.
    /// A count-based shortcut dropped that property, so <c>GET /api/logs</c> and
    /// <c>arcanum watch logs --tool</c> saw an empty structured bag for events like
    /// <c>"Provider {ProviderName} health changed."</c> while their multi-placeholder siblings kept
    /// theirs.
    /// </summary>
    [Fact]
    public void Two_property_event_keeps_its_structured_property()
    {

        RecordingBuffer buffer = new();

        SerilogLogRingBufferSink sink = new(
            buffer,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        sink.Emit(CreateEvent(
            "Provider {ProviderName} health changed.",
            new LogEventProperty("SourceContext", new ScalarValue("ProviderHealthTracker")),
            new LogEventProperty("ProviderName", new ScalarValue("openai"))));

        LogEntry entry = Assert.Single(buffer.Entries);

        Assert.Equal("ProviderHealthTracker", entry.Category);

        // Serilog renders scalar strings quoted; the point is that the property survives at all.
        Assert.Equal("\"openai\"", Assert.Contains("ProviderName", entry.Properties));

    }

    [Fact]
    public void Ambient_source_context_and_correlation_id_are_never_duplicated_into_properties()
    {

        RecordingBuffer buffer = new();

        SerilogLogRingBufferSink sink = new(
            buffer,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        sink.Emit(CreateEvent(
            "Tool {ToolName} finished.",
            new LogEventProperty("SourceContext", new ScalarValue("GrimoireTurnWriter")),
            new LogEventProperty("CorrelationId", new ScalarValue("abc123")),
            new LogEventProperty("ToolName", new ScalarValue("read_file"))));

        LogEntry entry = Assert.Single(buffer.Entries);

        Assert.Equal("abc123", entry.CorrelationId);

        Assert.Equal("\"read_file\"", Assert.Contains("ToolName", entry.Properties));

        Assert.DoesNotContain("SourceContext", entry.Properties);

        Assert.DoesNotContain("CorrelationId", entry.Properties);

    }

    private static LogEvent CreateEvent(string template, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse(template),
            properties);

    private sealed class RecordingBuffer : ILogRingBuffer
    {

        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public void Write(LogEntry entry) => _entries.Add(entry);

        public IReadOnlyList<LogEntry> GetSnapshot() => _entries;

        public async IAsyncEnumerable<LogEntry> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {

            await Task.CompletedTask.ConfigureAwait(false);

            foreach (LogEntry entry in _entries)
            {

                ct.ThrowIfCancellationRequested();

                yield return entry;

            }

        }

    }

}
