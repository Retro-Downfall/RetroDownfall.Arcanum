namespace RetroDownfall.Arcanum.Core.Cli;

/// <summary>
/// Structured output of the <c>arcanum doctor</c> command when invoked with <c>--json</c>.
/// </summary>
public sealed record DoctorCheck(string Name, string Status, string? Detail);

public sealed record DoctorReport(bool Healthy, IReadOnlyList<DoctorCheck> Checks);
