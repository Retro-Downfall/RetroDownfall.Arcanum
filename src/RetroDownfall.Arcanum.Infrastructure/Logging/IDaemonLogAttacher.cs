namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public interface IDaemonLogAttacher
{

    IDisposable BeginExecutionScope(string executionId);

}
