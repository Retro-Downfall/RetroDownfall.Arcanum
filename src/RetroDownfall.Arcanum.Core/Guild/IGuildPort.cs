using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Transport-neutral execution of already committed work. Implementations cannot mint, widen or replace a Commission.</summary>
public interface IGuildPort
{
    Task<Result<GuildDispatchObservation>> DispatchAsync(GuildExecutionHandle execution, GuildDispatchRequest request, CancellationToken cancellationToken);

    Task<Result<GuildDispatchObservation>> ContinueAsync(GuildExecutionHandle execution, GuildDispatchRequest dispatch, GuildContinueRequest request, CancellationToken cancellationToken);

    Task<Result<GuildDispatchObservation>> RequestCancellationAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation, Guid commissionId, Guid dispatchId, CancellationToken cancellationToken);

    Task<Result<GuildDispatchObservation>> ObserveAsync(GuildExecutionHandle execution, Guid commissionId, Guid dispatchId, CancellationToken cancellationToken);
}
