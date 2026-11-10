using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

internal interface IA2AGuildDiscoveryAdmission
{
    Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}

public sealed partial class A2AClientService
{
    internal async Task<Result<AgentCard>> DiscoverGuildCardAsync(string destination, CancellationToken ct,
        IA2AGuildDiscoveryAdmission? admission = null)
    {
        if (!IsConfiguredGuildDestination(destination, _options.CurrentValue.ResolveA2A().AllowedRemoteAgents))
            return new Error(ErrorCodes.Sending.AgentNotAllowed, "Guild discovery requires an explicitly configured remote destination.");

        if (admission is null)
            return new Error(ErrorCodes.Guild.InvalidRequest, "Guild discovery requires physical admission.");

        AgentCard? observed = null;

        Result<A2ADispatchResult> discovery = await RunGuardedAsync(destination, null, ct,
            (_, card, _, _, _, _) =>
            {
                observed = card;

                return Task.FromResult(Result<A2ADispatchResult>.Success(new(null, string.Empty)));
            }, admission, guildDiscovery: true).ConfigureAwait(false);

        return discovery.IsFailure ? discovery.Error : Result<AgentCard>.Success(observed!);
    }

    internal static bool IsConfiguredGuildDestination(string destination, string[]? configured) =>
        Uri.TryCreate(destination?.Trim(), UriKind.Absolute, out Uri? target)
        && configured?.Any(entry => Uri.TryCreate(entry?.Trim(), UriKind.Absolute, out Uri? expected)
            && string.Equals(target.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal)) == true;
}
