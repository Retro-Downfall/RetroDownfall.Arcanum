using System.Diagnostics;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using Serilog.Core;
using Serilog.Events;
namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public sealed class SerilogLogRingBufferSink(
    ILogRingBuffer buffer,
    IOptionsMonitor<ArcanumSettings> settings) : ILogEventSink
{

    public void Emit(LogEvent logEvent)
    {

        try
        {

            LogLevel arcanumLevel = MapLevel(logEvent.Level);

            LogLevel minLevel = settings.CurrentValue.Logs?.MinLevelInBuffer ?? LogLevel.Information;

            if (arcanumLevel < minLevel)
            {

                return;
            }

            string category = logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext)
                ? (sourceContext as ScalarValue)?.Value as string ?? "Unknown"
                : "Unknown";

            Dictionary<string, string> properties;

            if (logEvent.Properties.Count <= 2)
            {

                properties = [];

            }
            else
            {

                properties = new Dictionary<string, string>(logEvent.Properties.Count, StringComparer.Ordinal);

                foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
                {

                    if (property.Key is "SourceContext" or "CorrelationId")
                    {

                        continue;

                    }

                    properties[property.Key] = property.Value.ToString(null, null) ?? string.Empty;

                }

            }

            LogEntry entry = new(
                Sequence: 0,
                Timestamp: logEvent.Timestamp,
                Level: arcanumLevel,
                Category: category,
                Message: logEvent.RenderMessage(),
                Exception: logEvent.Exception?.ToString(),
                CorrelationId: GetCorrelationId(logEvent),
                TraceId: logEvent.TraceId?.ToString() ?? Activity.Current?.Id,
                Properties: properties);

            buffer.Write(entry);
        }
        catch
        {
            // Sinks must be safe — do not propagate.
        }
    }

    private static string? GetCorrelationId(LogEvent logEvent)
    {

        if (logEvent.Properties.TryGetValue("CorrelationId", out LogEventPropertyValue? value)
            && value is ScalarValue scalar
            && scalar.Value is string s)
        {

            return s;
        }

        return null;
    }

    private static LogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.Information,
    };

}
