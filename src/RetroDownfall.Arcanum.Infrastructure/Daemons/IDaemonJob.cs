namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public interface IDaemonJob
{

    string Id { get; }

    string Name { get; }

    string? Description { get; }

    bool CanRunOnDemand { get; }

    string TargetSpell { get; }

    Task RunAsync(CancellationToken ct);

}
