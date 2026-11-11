using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

internal sealed record A2AGuildSendingRequest(string AgentUrl, string Body, string MessageId, GuildBudgetGrant Grant,
    string? TaskId = null, string? ContextId = null, A2ADispatchMode Mode = A2ADispatchMode.Blocking,
    A2ASendingOptions? Options = null, IReadOnlyList<string>? DelegationChain = null,
    GuildRemoteTransportDescriptor? Transport = null);

internal sealed record A2AGuildTaskObservation(string? TaskId, string? ContextId, string State, string ResponseText,
    A2ARemoteCost Cost, DateTimeOffset ObservedAt, int? DesignatedResultCount = null,
    byte[]? DesignatedResultUtf8 = null, string? DesignatedResultMimeType = null);

/// <summary>Mandatory durable host participant; transport cannot authorize or settle a Quest.</summary>
internal interface IA2AGuildSendingLifecycle
{
    Task<Result> PreflightAsync(CancellationToken ct);

    Task<Result<SendMessageResponse>> AdmitAndSendAsync(Func<CancellationToken, Task<SendMessageResponse>> send, CancellationToken ct);

    Task<Result<T>> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken ct);

    Task<Result> AcceptedAsync(string? taskId, string? contextId, CancellationToken ct);

    Task<Result> CallbackAsync(string configId, string tokenHash, CancellationToken ct);

    Task<Result> ObservedAsync(A2AGuildTaskObservation observation, CancellationToken ct);
}

public sealed partial class A2AClientService
{
    internal async Task<Result<A2ADispatchResult>> SendGuildAsync(A2AGuildSendingRequest request,
        IA2AGuildSendingLifecycle lifecycle, CancellationToken ct)
    {
        if (request is null || lifecycle is null || string.IsNullOrWhiteSpace(request.Body)
            || string.IsNullOrWhiteSpace(request.MessageId) || !Enum.IsDefined(request.Mode))
            return new Error(ErrorCodes.Guild.InvalidRequest, "The transport request must identify committed Guild work.");

        if (request.Grant.Usd.Mode != GuildBudgetMode.Unlimited || request.Grant.Tokens.Mode != GuildBudgetMode.Unlimited)
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A transport cannot enforce a finite USD or token grant.");

        if (!_options.CurrentValue.Features.Guild)
            return new Error(ErrorCodes.Guild.Unavailable, "Guild execution is disabled.");

        GuardedGuildLifecycle guarded = new(this, request.AgentUrl, request.Transport is not null, lifecycle);

        Result admitted = await guarded.PreflightAsync(ct).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        return await RunGuardedAsync(request.AgentUrl, request.DelegationChain, ct,
            (client, card, url, chain, slot, token) =>
            {
                guarded.Card = card;

                Result target = ValidateGuildTarget(card, request.Transport);

                if (target.IsFailure) return Task.FromResult(Result<A2ADispatchResult>.Failure(target.Error));

                Result<A2AOutboundModality> modality = NegotiateAsync(card, request.Options);

                if (modality.IsFailure) return Task.FromResult(Result<A2ADispatchResult>.Failure(modality.Error));

                Message message = new()
                {
                    Role = Role.User,
                    MessageId = request.MessageId,
                    TaskId = request.TaskId,
                    ContextId = request.ContextId,
                    Parts = [Part.FromText(request.Body)],
                };

                ConclaveDelegationChain.Write(message, ConclaveDelegationChain.Extend(chain));

                return ExchangeAsync(client, card, message, url, null, request.Mode, modality.Value,
                    null, slot, token, guarded, guildExecution: true);
            }, new GuildDiscoveryAdmission(guarded, false), guildDiscovery: true).ConfigureAwait(false);
    }

    internal async Task<Result<A2ADispatchResult>> ObserveGuildTaskAsync(string agentUrl, string taskId,
        GuildRemoteTransportDescriptor transport, IA2AGuildSendingLifecycle lifecycle, bool cancellationRequested,
        CancellationToken ct, string? expectedContextId = null)
    {
        if (string.IsNullOrWhiteSpace(taskId) || lifecycle is null)
            return new Error(ErrorCodes.Guild.InvalidRequest, "A durable remote task binding is required.");

        GuardedGuildLifecycle guarded = new(this, agentUrl, true, lifecycle, true);

        Result admitted = await guarded.PreflightAsync(ct).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        return await RunGuardedAsync(agentUrl, null, ct, async (client, card, _, _, _, token) =>
        {
            guarded.Card = card;

            Result target = ValidateGuildTarget(card, transport);

            if (target.IsFailure) return target.Error;

            try
            {
                if (cancellationRequested)
                {
                    try
                    {
                        AgentTask cancelled = await WithGuildObservationAsync(
                            observationToken => client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, observationToken),
                            guarded, true, token, guildExecution: true).ConfigureAwait(false);

                        RequireGuildTaskIdentity(cancelled, taskId, expectedContextId, guarded);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or A2AException or System.Text.Json.JsonException)
                    {
                        // A lost acknowledgement or an already-terminal refusal proves no stop.
                        // The exact bound task is still queried before any result or cost settles.
                    }
                }

                // A completed cancel request does not authorize a later request. Recheck current
                // stopping authority for the authoritative observation after cancellation settles.
                AgentTask task = await WithGuildObservationAsync(
                    observationToken => client.GetTaskAsync(new GetTaskRequest { Id = taskId }, observationToken),
                    guarded, true, token, guildExecution: true).ConfigureAwait(false);

                RequireGuildTaskIdentity(task, taskId, expectedContextId, guarded);

                Result observed = await guarded.ObservedAsync(GuildObservation(task), CancellationToken.None).ConfigureAwait(false);

                return observed.IsFailure ? observed.Error : new A2ADispatchResult(task.Id, ExtractTaskText(task), A2ASendingUsageMetadata.ReadGuild(task));
            }
            catch (GuildTransportAdmissionException ex)
            {
                return ex.Error;
            }
            catch (Exception ex) when (ex is HttpRequestException or A2AException or System.Text.Json.JsonException)
            {
                return new Error(ErrorCodes.Sending.AgentUnreachable, "The bound remote task could not be observed.");
            }
        }, new GuildDiscoveryAdmission(guarded, true), guildDiscovery: true, existingGuildTask: true).ConfigureAwait(false);
    }

    private Result ValidateCurrentGuildDestination(string discovery, AgentCard? card, bool required, bool existingTask)
    {
        ArcanumSettings settings = _options.CurrentValue;

        if (!existingTask && !GuildRemotePort.IsTransportEnabled(settings))
            return new Error(ErrorCodes.Sending.Disabled, "Remote Guild execution is disabled.");

        string[] allowed = settings.ResolveA2A().AllowedRemoteAgents ?? [];

        if ((required || allowed.Length > 0) && !IsConfiguredGuildDestination(discovery, allowed))
            return new Error(ErrorCodes.Sending.AgentNotAllowed, "The accepted remote destination is no longer configured.");

        if (card?.SupportedInterfaces is { } interfaces && allowed.Length > 0
            && interfaces.Any(item => !IsAllowedAgent(item.Url, allowed)))
            return new Error(ErrorCodes.Sending.AgentNotAllowed, "An accepted peer interface is no longer allowed.");

        return Result.Success();
    }

    private sealed class GuardedGuildLifecycle(A2AClientService owner, string destination, bool configuredRequired,
        IA2AGuildSendingLifecycle inner, bool observationOnly = false)
    {
        internal AgentCard? Card { get; set; }

        public async Task<Result> PreflightAsync(CancellationToken ct)
        {
            Result current = owner.ValidateCurrentGuildDestination(destination, Card, configuredRequired, observationOnly);

            return current.IsFailure ? current : await inner.PreflightAsync(ct).ConfigureAwait(false);
        }

        public Task<Result<SendMessageResponse>> AdmitAndSendAsync(Func<CancellationToken, Task<SendMessageResponse>> send, CancellationToken ct) =>
            inner.AdmitAndSendAsync(token =>
            {
                Result current = owner.ValidateCurrentGuildDestination(destination, Card, configuredRequired, false);

                return current.IsFailure ? Task.FromException<SendMessageResponse>(new GuildTransportAdmissionException(current.Error)) : send(token);
            }, ct);

        public Task<Result<T>> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken ct) =>
            inner.AdmitAuxiliaryAsync(token =>
            {
                Result current = owner.ValidateCurrentGuildDestination(destination, Card, configuredRequired, stopping || observationOnly);

                return current.IsFailure ? Task.FromException<T>(new GuildTransportAdmissionException(current.Error)) : action(token);
            }, stopping, ct);

        public Task<Result> AcceptedAsync(string? taskId, string? contextId, CancellationToken ct) => inner.AcceptedAsync(taskId, contextId, ct);

        public Task<Result> CallbackAsync(string configId, string tokenHash, CancellationToken ct) => inner.CallbackAsync(configId, tokenHash, ct);

        public Task<Result> ObservedAsync(A2AGuildTaskObservation observation, CancellationToken ct) => inner.ObservedAsync(observation, ct);
    }

    private sealed class GuildTransportAdmissionException(Error error) : Exception("The remote transport observation was not admitted.")
    {
        internal Error Error { get; } = error;
    }

    private sealed class GuildDiscoveryAdmission(GuardedGuildLifecycle lifecycle, bool stopping) : IA2AGuildDiscoveryAdmission
    {
        public async Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Result<T> admitted = await lifecycle.AdmitAuxiliaryAsync(action, stopping, ct).ConfigureAwait(false);

            return admitted.IsFailure ? throw new GuildTransportAdmissionException(admitted.Error) : admitted.Value;
        }
    }

    private static async Task<T> WithGuildObservationAsync<T>(Func<CancellationToken, Task<T>> action,
        GuardedGuildLifecycle? guild, bool stopping, CancellationToken ct, bool guildExecution)
    {
        if (!guildExecution) return await action(ct).ConfigureAwait(false);

        if (guild is null) throw new InvalidOperationException("Guild execution requires its transport lifecycle.");

        Result<T> admitted = await guild.AdmitAuxiliaryAsync(action, stopping, ct).ConfigureAwait(false);

        return admitted.IsFailure ? throw new GuildTransportAdmissionException(admitted.Error) : admitted.Value;
    }

    private static void RequireGuildTaskIdentity(AgentTask task, string taskId, string? contextId, GuardedGuildLifecycle? guild)
    {
        if (guild is not null && (task.Id != taskId || contextId is not null && task.ContextId != contextId || task.Status is null))
            throw new GuildTransportAdmissionException(new(ErrorCodes.Guild.ReconciliationRequired,
                "The peer response does not bind the accepted remote task and context."));
    }

    private static void RequireGuildStreamIdentity(StreamResponse update, AgentTask expected, GuardedGuildLifecycle? guild)
    {
        if (guild is null) return;

        (string? task, string? context) = update.PayloadCase switch
        {
            StreamResponseCase.Task => (update.Task?.Id, update.Task?.ContextId),
            StreamResponseCase.StatusUpdate => (update.StatusUpdate?.TaskId, update.StatusUpdate?.ContextId),
            StreamResponseCase.ArtifactUpdate => (update.ArtifactUpdate?.TaskId, update.ArtifactUpdate?.ContextId),
            StreamResponseCase.Message => (update.Message?.TaskId, update.Message?.ContextId),
            _ => (null, null),
        };

        if (task != expected.Id || expected.ContextId is not null && context != expected.ContextId)
            throw new GuildTransportAdmissionException(new(ErrorCodes.Guild.ReconciliationRequired,
                "The peer stream does not bind the accepted remote task and context."));
    }

    internal static A2AGuildTaskObservation GuildObservation(AgentTask task)
    {
        GuildFinalOutput? output = GuildFinalOutput.FromTask(task);

        return new(task.Id, task.ContextId, task.Status.State == TaskState.AuthRequired ? "input-required" : StateName(task.Status.State),
            ExtractTaskText(task), A2ASendingUsageMetadata.ReadGuild(task), DateTimeOffset.UtcNow,
            output is null ? null : 1, output?.Utf8, output?.MimeType);
    }

    private static bool UnsupportedGuildState(TaskState state) => state != TaskState.AuthRequired
        && GuildRemoteStateProjection.Observe(StateName(state)).ReconciliationRequired;

    private static Result ValidateGuildTarget(AgentCard card, GuildRemoteTransportDescriptor? expected)
    {
        if (expected is null) return Result.Success();

        if (card.SupportedInterfaces is not { Count: > 0 } interfaces)
            return new Error(ErrorCodes.Guild.DestinationChanged, "The peer no longer advertises the accepted transport.");

        bool matches = expected.InterfaceObservations.IsDefault
            ? interfaces.All(item => Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? actual)
                && Uri.TryCreate(expected.Endpoint, UriKind.Absolute, out Uri? frozen) && actual == frozen
                && expected.Interfaces.Contains(item.ProtocolBinding, StringComparer.Ordinal)
                && item.ProtocolVersion == expected.ProtocolVersion)
            : interfaces.Select(item => new GuildRemoteInterfaceDescriptor(item.Url, item.ProtocolBinding, item.ProtocolVersion))
                .SequenceEqual(expected.InterfaceObservations);

        return matches ? Result.Success()
            : new Error(ErrorCodes.Guild.DestinationChanged, "The peer no longer advertises the accepted transport.");
    }
}
