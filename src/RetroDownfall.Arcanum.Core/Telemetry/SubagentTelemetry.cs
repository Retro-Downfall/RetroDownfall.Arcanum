namespace RetroDownfall.Arcanum.Core.Telemetry;

public enum SubagentRunOutcome
{
    Completed = 0,
    Failed = 1,
    BudgetExhausted = 2,
    Cancelled = 3,
}

public sealed record SubagentTelemetryEvent(
    long Tokens,
    decimal CostUsd,
    TimeSpan Latency,
    SubagentRunOutcome Outcome);

public interface ISubagentTelemetrySink
{
    void RecordSubagentRun(SubagentTelemetryEvent telemetryEvent);
}

public sealed record SubagentTelemetrySnapshot(
    long Runs = 0,
    long Completed = 0,
    long Failed = 0,
    long BudgetExhausted = 0,
    long Tokens = 0,
    decimal CostUsd = 0,
    TimeSpan CumulativeLatency = default);
