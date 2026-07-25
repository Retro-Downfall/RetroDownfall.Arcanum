using RetroDownfall.Arcanum.Infrastructure.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace RetroDownfall.Arcanum.Tests.Logging;

public sealed class DaemonLogAttacherTests
{

    [Fact]
    public void BeginExecutionScope_attaches_correlation_id_and_restores_outer_context()
    {

        CapturingSink sink = new();

        using Logger logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        DaemonLogAttacher attacher = new();

        using (LogContext.PushProperty("CorrelationId", "outer"))
        {

            using (attacher.BeginExecutionScope("execution-42"))
            {

                logger.Information("inside daemon execution");

            }

            logger.Information("after daemon execution");

        }

        logger.Information("outside correlation scopes");

        Assert.Collection(
            sink.Events,
            inside => AssertCorrelationId(inside, "execution-42"),
            after => AssertCorrelationId(after, "outer"),
            outside => Assert.DoesNotContain("CorrelationId", outside.Properties));

    }

    private static void AssertCorrelationId(LogEvent logEvent, string expected)
    {

        Assert.True(logEvent.Properties.TryGetValue("CorrelationId", out LogEventPropertyValue? value));

        ScalarValue scalar = Assert.IsType<ScalarValue>(value);

        Assert.Equal(expected, scalar.Value);

    }

    private sealed class CapturingSink : ILogEventSink
    {

        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

    }

}
