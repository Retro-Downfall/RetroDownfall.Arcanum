using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Routes only after authenticating the handle and reading its committed execution binding.</summary>
internal sealed class GuildPortRouter(GuildQuestStore quests, GuildLocalHostPolicy callers, SignetAuthority authority,
    GuildLocalPort local, GuildRemotePort remote) : IGuildPort
{
    private async Task<Result<IGuildPort>> ResolveAsync(GuildExecutionHandle execution, Guid invocation, CancellationToken ct)
    {
        if (execution is null || invocation == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "A committed execution identity is required.");

        var caller = await callers.CreateRunCallerAsync(authority, execution, invocation, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var kind = await quests.IsRemoteExecutionAsync(new(execution, "observe", 0, caller.Value), ct).ConfigureAwait(false);

        return kind.IsFailure ? kind.Error : Result<IGuildPort>.Success(kind.Value ? remote : local);
    }

    public async Task<Result<GuildDispatchObservation>> DispatchAsync(GuildExecutionHandle execution, GuildDispatchRequest request, CancellationToken ct)
    {
        var port = await ResolveAsync(execution, request?.DispatchId ?? Guid.Empty, ct).ConfigureAwait(false);

        return port.IsFailure ? port.Error : await port.Value.DispatchAsync(execution, request!, ct).ConfigureAwait(false);
    }

    public async Task<Result<GuildDispatchObservation>> ContinueAsync(GuildExecutionHandle execution, GuildDispatchRequest dispatch,
        GuildContinueRequest request, CancellationToken ct)
    {
        var port = await ResolveAsync(execution, dispatch?.DispatchId ?? Guid.Empty, ct).ConfigureAwait(false);

        return port.IsFailure ? port.Error : await port.Value.ContinueAsync(execution, dispatch!, request, ct).ConfigureAwait(false);
    }

    public async Task<Result<GuildDispatchObservation>> RequestCancellationAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation,
        Guid questId, Guid dispatchId, CancellationToken ct)
    {
        var port = await ResolveAsync(execution, dispatchId, ct).ConfigureAwait(false);

        return port.IsFailure ? port.Error : await port.Value.RequestCancellationAsync(execution, mutation, questId, dispatchId, ct).ConfigureAwait(false);
    }

    public async Task<Result<GuildDispatchObservation>> ObserveAsync(GuildExecutionHandle execution, Guid questId, Guid dispatchId, CancellationToken ct)
    {
        var port = await ResolveAsync(execution, dispatchId, ct).ConfigureAwait(false);

        return port.IsFailure ? port.Error : await port.Value.ObserveAsync(execution, questId, dispatchId, ct).ConfigureAwait(false);
    }
}
