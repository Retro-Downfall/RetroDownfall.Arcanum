using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-controlled discovery of an explicitly configured outbound destination.</summary>
internal sealed class GuildConfiguredRemoteResolver(GuildLocalHostPolicy policy, GuildStore store,
    A2AClientService client, IOptionsMonitor<ArcanumSettings> settings, SignetAuthority? authority = null)
{
    internal async Task<Result<GuildAgentCard>> ResolveAsync(GuildCallerContext caller, string destination, CancellationToken ct,
        GuildChildToolAdmission? toolAdmission = null)
    {
        if (!GuildRemotePort.IsTransportEnabled(settings.CurrentValue) || !policy.Authenticates(caller) || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run))
            return Forbidden;

        if (toolAdmission is not null && caller.Kind != GuildCallerKind.Run) return Forbidden;

        if (caller.Kind == GuildCallerKind.Run)
        {
            if (authority is null || caller.Execution is null) return Forbidden;

            Result<bool> live = await authority.InQuestTransactionAsync<bool>(caller.Execution.RootQuestId,
                (connection, transaction, signets, token) => AuthorizeRunAsync(connection, transaction, caller, destination, signets, toolAdmission, token), ct).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;
        }

        Result<AgentCard> discovery;

        try
        {
            discovery = await client.DiscoverGuildCardAsync(destination, ct, caller.Kind == GuildCallerKind.Run
                ? new RunDiscoveryAdmission(this, caller, destination, toolAdmission)
                : new OperatorDiscoveryAdmission(this, caller, destination)).ConfigureAwait(false);
        }
        catch (DiscoveryRefusedException refused)
        {
            return refused.Failure;
        }

        if (discovery.IsFailure) return discovery.Error;

        ConclaveA2ASettings configured = settings.CurrentValue.ResolveA2A();

        if (!A2AClientService.IsConfiguredGuildDestination(destination, configured.AllowedRemoteAgents))
            return Changed;

        string endpoint = new Uri(destination.Trim()).AbsoluteUri;

        // Installation-controlled discovery identity remains stable when a peer edits its Card prose.
        string peerId = "outbound:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));

        AgentCard card = discovery.Value;

        AgentInterface first = card.SupportedInterfaces![0];

        GuildRemoteTransportDescriptor transport = new(peerId, first.Url, first.ProtocolVersion,
            [.. card.SupportedInterfaces.Select(item => item.ProtocolBinding)], [],
            string.IsNullOrWhiteSpace(configured.OutboundCredentialEnvironmentVariable) ? null : configured.OutboundCredentialEnvironmentVariable.Trim(),
            null, null, [.. card.SupportedInterfaces.Select(item => new GuildRemoteInterfaceDescriptor(item.Url, item.ProtocolBinding, item.ProtocolVersion))]);

        GuildConfiguredRemoteSource source = new(endpoint, transport);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(card,
            (JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)));

        Result<GuildRemoteCardObservation> parsed = GuildRemoteCardReader.Parse(source, bytes);

        if (parsed.IsFailure) return parsed.Error;

        GuildStoredAgentCardPayload payload = new(parsed.Value.Card, string.Empty, [], transport, endpoint);

        // The digest covers the host-controlled transport observation as well as Card content.
        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload,
            GuildStorageJsonContext.Default.GuildStoredAgentCardPayload)));

        GuildCardWrite write = new(GuildRemoteCardReader.SourceIdentity(source), null, null, null, payload, digest);

        Result<GuildStoredCard> saved = caller.Kind == GuildCallerKind.Run
            ? await authority!.InQuestTransactionAsync<GuildStoredCard>(caller.Execution!.RootQuestId,
                async (connection, transaction, signets, token) =>
                {
                    Result<bool> live = await AuthorizeRunAsync(connection, transaction, caller, endpoint, signets, toolAdmission, token).ConfigureAwait(false);

                    return live.IsFailure ? live.Error
                        : await store.UpsertWithinAsync(connection, transaction, write, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false)
            : await store.UpsertConfiguredRemoteAsync(policy, caller, write,
                () => GuildRemotePort.IsTransportEnabled(settings.CurrentValue) && A2AClientService.IsConfiguredGuildDestination(endpoint,
                    settings.CurrentValue.ResolveA2A().AllowedRemoteAgents), ct).ConfigureAwait(false);

        return saved.IsFailure ? saved.Error : saved.Value.Payload.Card;
    }

    private async Task<T> AdmitRunDiscoveryAsync<T>(GuildCallerContext caller, string destination,
        GuildChildToolAdmission? toolAdmission, Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        Result<T> response = await authority!.AdmitQuestEffectAsync<T>(caller.Execution!.RootQuestId,
            (connection, transaction, signets, token) => AuthorizeRunAsync(connection, transaction, caller, destination, signets, toolAdmission, token),
            async token => Result<T>.Success(await send(token).ConfigureAwait(false)), ct).ConfigureAwait(false);

        if (response.IsFailure) throw new DiscoveryRefusedException(response.Error);

        return response.Value;
    }

    private async Task<T> AdmitOperatorDiscoveryAsync<T>(GuildCallerContext caller, string destination,
        Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        Result<T> response = await store.AdmitConfiguredRemoteDiscoveryAsync(policy, caller,
            () => GuildRemotePort.IsTransportEnabled(settings.CurrentValue)
                && A2AClientService.IsConfiguredGuildDestination(destination, settings.CurrentValue.ResolveA2A().AllowedRemoteAgents),
            send, ct).ConfigureAwait(false);

        if (response.IsFailure) throw new DiscoveryRefusedException(response.Error);

        return response.Value;
    }

    private sealed class RunDiscoveryAdmission(GuildConfiguredRemoteResolver owner, GuildCallerContext caller,
        string destination, GuildChildToolAdmission? toolAdmission) : IA2AGuildDiscoveryAdmission
    {
        public Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            owner.AdmitRunDiscoveryAsync(caller, destination, toolAdmission, action, ct);
    }

    private sealed class OperatorDiscoveryAdmission(GuildConfiguredRemoteResolver owner, GuildCallerContext caller,
        string destination) : IA2AGuildDiscoveryAdmission
    {
        public Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            owner.AdmitOperatorDiscoveryAsync(caller, destination, action, ct);
    }

    private async Task<Result<bool>> AuthorizeRunAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, string destination, SignetAuthority.QuestTransaction signets,
        GuildChildToolAdmission? toolAdmission, CancellationToken ct)
    {
        if (!GuildRemotePort.IsTransportEnabled(settings.CurrentValue) || !policy.Authenticates(caller) || caller.Execution is null) return Forbidden;

        if (!A2AClientService.IsConfiguredGuildDestination(destination, settings.CurrentValue.ResolveA2A().AllowedRemoteAgents)) return Changed;

        if (toolAdmission is not null)
        {
            if (toolAdmission.Kind != GuildLocalChildToolKind.AdmitCohortMember
                || !ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                || !GuildTaskArguments.IsRemote(toolAdmission.Arguments)
                || !Uri.TryCreate(toolAdmission.Arguments.GetProperty("agent").GetString()!.Trim(), UriKind.Absolute, out Uri? intended)
                || !Uri.TryCreate(destination.Trim(), UriKind.Absolute, out Uri? actual) || intended.AbsoluteUri != actual.AbsoluteUri)
                return Forbidden;

            var liveTool = await toolAdmission.Registry.Parent.Store.AuthorizeCohortToolWithinAsync(connection, transaction,
                signets, toolAdmission, ct).ConfigureAwait(false);

            if (liveTool.IsFailure) return liveTool.Error;
        }

        BudgetSettings daily = settings.CurrentValue.ResolveBudget();

        if (daily.Enabled && daily.DailyLimitUsd > 0)
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A cannot reserve finite daily spending authority.");

        Result<GuildGrant> grant = await signets.VerifyAsync(caller.Execution, ct).ConfigureAwait(false);

        if (grant.IsFailure) return grant.Error;

        if (!grant.Value.Tools.Contains(toolAdmission?.Tool ?? new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task"))
            || !grant.Value.Delegation.Contains(GuildDelegationKind.Remote)) return Forbidden;

        if (grant.Value.Budget.Usd.Mode != GuildBudgetMode.Unlimited || grant.Value.Budget.Tokens.Mode != GuildBudgetMode.Unlimited)
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A cannot enforce finite spending authority.");

        return true;
    }

    private sealed class DiscoveryRefusedException(Error failure) : Exception("Guild discovery authority is unavailable.")
    {
        internal Error Failure { get; } = failure;
    }

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "A current host-issued Guild caller is required for remote discovery.");

    private static Error Changed => new(ErrorCodes.Guild.DestinationChanged, "The configured remote destination changed during discovery.");
}
