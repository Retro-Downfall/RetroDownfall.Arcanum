using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

internal interface IA2AGuildDiscoveryAdmission
{
    Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}

internal sealed record A2AGuildCardObservation(AgentCard Card, bool PortalMessagesV1);

public sealed partial class A2AClientService
{
    internal async Task<Result<AgentCard>> DiscoverGuildCardAsync(string destination, CancellationToken ct,
        IA2AGuildDiscoveryAdmission? admission = null)
    {
        var observed = await DiscoverGuildTransportAsync(destination, ct, admission).ConfigureAwait(false);

        return observed.IsFailure ? observed.Error : observed.Value.Card;
    }

    internal Task<Result<A2AGuildCardObservation>> DiscoverGuildTransportAsync(string destination, CancellationToken ct,
        IA2AGuildDiscoveryAdmission? admission)
    {
        if (!IsConfiguredGuildDestination(destination, _options.CurrentValue.ResolveA2A().AllowedRemoteAgents))
            return Task.FromResult(Result<A2AGuildCardObservation>.Failure(
                new(ErrorCodes.Sending.AgentNotAllowed, "Guild discovery requires an explicitly configured remote destination.")));

        if (admission is null)
            return Task.FromResult(Result<A2AGuildCardObservation>.Failure(
                new(ErrorCodes.Guild.InvalidRequest, "Guild discovery requires physical admission.")));

        bool portalMessages = false;

        return RunGuardedAsync<A2AGuildCardObservation>(destination, null, ct,
            (_, card, _, _, _, _) => Task.FromResult(Result<A2AGuildCardObservation>.Success(new(card, portalMessages && HasPortalCredential(_options.CurrentValue)))),
            admission, guildDiscovery: true, observePortalCapability: qualified => portalMessages = qualified);
    }

    internal static bool IsConfiguredGuildDestination(string destination, string[]? configured) =>
        Uri.TryCreate(destination?.Trim(), UriKind.Absolute, out Uri? target)
        && configured?.Any(entry => Uri.TryCreate(entry?.Trim(), UriKind.Absolute, out Uri? expected)
            && string.Equals(target.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal)) == true;
}
