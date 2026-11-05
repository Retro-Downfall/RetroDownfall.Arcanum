using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Live exact-tool authorization. Boundary availability is supplied only by host-owned effect adapters.</summary>
public interface IGuildToolAuthority
{
    Task<Result> VerifyToolAsync(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable, CancellationToken cancellationToken);

    /// <summary>The short callback hands off an admitted effect; it must not wait for a provider, process or network response.</summary>
    Task<Result<T>> AdmitToolAsync<T>(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable,
        Func<GuildVerifiedAuthority, CancellationToken, Task<Result<T>>> effect, CancellationToken cancellationToken);
}
