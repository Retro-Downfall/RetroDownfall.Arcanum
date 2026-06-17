using Serilog.Context;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public sealed class DaemonLogAttacher : IDaemonLogAttacher
{

    public IDisposable BeginExecutionScope(string executionId) =>
        LogContext.PushProperty("CorrelationId", executionId);

}
