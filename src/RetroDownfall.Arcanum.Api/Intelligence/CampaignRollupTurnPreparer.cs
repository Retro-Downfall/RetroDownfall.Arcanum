using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>One claimed maintenance snapshot and optional Campaign artifact for every dispatch of a logical turn.</summary>
public sealed class CampaignRollupTurnSnapshot(
    CampaignRollupArtifact? artifact,
    CanonicalCampaignContext campaign,
    PingRequest request,
    ICampaignRollupStore store,
    IOptionsMonitor<ArcanumSettings> settings,
    ICanonicalCampaignContextResolver resolver,
    CovenantTurnLease? authority,
    SessionTurnClaimLease? claimLease = null,
    ISessionTurnClaimCoordinator? claims = null,
    CovenantDigest? frozenConfigurationDigest = null,
    bool requiresRollups = true,
    LongRestConfigurationStamp? longRestStamp = null,
    ILongRestConfigurationProvider? longRestConfiguration = null,
    ICanonicalCampaignRootEnricher? rootEnricher = null) : IAsyncDisposable
{
    private readonly CovenantDigest _configurationDigest = frozenConfigurationDigest
        ?? CampaignRollupTurnPreparer.ConfigurationDigest(settings.CurrentValue);

    public CampaignRollupArtifact? Artifact { get; } = artifact;

    public Guid? SessionId => request.SessionId;

    public CanonicalCampaignContext Campaign { get; } = campaign;

    public CovenantTurnLease? Authority { get; } = authority;

    public SessionTurnClaimLease? ClaimLease { get; } = claimLease;

    public Result<PromptTurnResult>? Replay { get; internal set; }

    private bool _begun;

    public void MarkBegun() => _begun = true;

    private SessionTurnClaimOutcome? _terminalOutcome;

    internal void RecordTerminal(Error error) => _terminalOutcome = SessionTurnClaimOutcome.Discarded(
        error.Code, Primitives.ArcanumErrorMapper.ResolveStatusCode(error.Code), []);

    private Result ValidateConfiguration()
    {
        ArcanumSettings configuration = settings.CurrentValue;

        if ((requiresRollups && !configuration.ResolveIntelligence().EnableCampaignRollups)
            || CampaignRollupTurnPreparer.ConfigurationDigest(configuration) != _configurationDigest)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "The configuration bound to this turn changed.");
        }

        if (longRestStamp is not null && (!configuration.Features.LongRest
            || longRestConfiguration is null || !longRestConfiguration.IsCurrent(longRestStamp)))
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "The maintenance configuration accepted by this turn is no longer current.");
        }

        return Result.Success();
    }

    public async Task<Result> ValidateAsync(CancellationToken cancellationToken)
    {
        Result configuration = ValidateConfiguration();

        if (configuration.IsFailure)
        {
            return configuration;
        }

        if (Authority is not null)
        {
            Result valid = await Authority.RevalidateAsync(cancellationToken).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                return valid;
            }
        }

        Result<CanonicalCampaignContext> current = await resolver.ResolveAsync(
            new(request.SessionId, request.CampaignId, request.WorkingDirectory), cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {
            return current.Error;
        }

        if (longRestStamp is not null)
        {
            // A turn that accepted Long Rest carries its Campaign's registered root (the boundary enriched it), so the fence that
            // revalidates the turn reads the root the same way. A snapshot that cannot do so cannot prove its Campaign is unchanged.
            if (rootEnricher is null)
            {
                return new Error(ErrorCodes.Covenant.StaleSnapshot, "This turn's canonical Campaign identity cannot be revalidated.");
            }

            current = await rootEnricher.EnrichAsync(current.Value, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }
        }

        if (current.Value != Campaign)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "This turn's canonical Campaign identity changed.");
        }

        if (Artifact is not null)
        {
            Result artifact = await store.ValidateAsync(Artifact, Authority, cancellationToken).ConfigureAwait(false);

            if (artifact.IsFailure)
            {
                return artifact;
            }
        }

        // Every awaited validator can observe a configuration transition. Recheck the full
        // settings and the monotonic Long Rest stamp at the actual successful dispatch boundary.
        return ValidateConfiguration();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_begun && Replay is null && ClaimLease is { IsExecutable: true } lease && claims is not null)
            {
                _ = await claims.CompleteAsync(lease,
                    _terminalOutcome ?? SessionTurnClaimOutcome.Discarded(ErrorCodes.Hub.SessionTurnRestoredInterrupted, 409, []),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Authority is not null)
            {
                await Authority.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Lets the caller of <see cref="CampaignRollupTurnPreparer.PrepareLiveAsync"/> learn, while preparation is still running, that
/// protected Long Rest maintenance is about to run under this turn, so a streaming client can be told before a long wait. With
/// <paramref name="holdUntilAcknowledged"/> the preparer also waits, for a bounded time, until the caller acknowledges that it
/// has told the client, so the telling genuinely precedes the first paid protected work instead of racing it. Only a streaming
/// caller has a client to tell, so only a streaming caller asks to be held.
/// </summary>
public sealed class CampaignPreparationProgress(bool holdUntilAcknowledged = false)
{
    private static readonly TimeSpan AcknowledgementBound = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _protectedMaintenance = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once, when the first protected partition of the turn that still has maintenance to finish is about to be resumed. A
    /// partition whose sweep is already completed has none, and never completes it.
    /// </summary>
    public Task ProtectedMaintenanceStarted => _protectedMaintenance.Task;

    /// <summary>The caller has told its client; a preparer that was holding for it continues.</summary>
    public void AcknowledgeProtectedMaintenance() => _acknowledged.TrySetResult();

    internal async Task SignalProtectedMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (!_protectedMaintenance.TrySetResult() || !holdUntilAcknowledged)
        {
            return;
        }

        try
        {
            await _acknowledged.Task.WaitAsync(AcknowledgementBound, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A caller that never acknowledges (its client went away) must not stall the turn's maintenance.
        }
    }
}

/// <summary>
/// The shared snapshot preparation seam for live inference and read-only inspection. The five Long Rest ports (the sweep coordinator,
/// the maintenance, the discovery store, the configuration epoch and the Campaign root enricher) are required: the composed host
/// always registers them, and a turn never discovers at preparation time that Long Rest has no coordinator.
/// </summary>
public sealed class CampaignRollupTurnPreparer(
    ICampaignRollupStore store,
    IOptionsMonitor<ArcanumSettings> settings,
    ICanonicalCampaignContextResolver resolver,
    ICovenantOperationGate gate,
    ILongRestSweepCoordinator longRest,
    ILongRestMaintenance longRestMaintenance,
    ILongRestDiscoveryStore longRestDiscovery,
    ILongRestConfigurationProvider longRestConfiguration,
    ICanonicalCampaignRootEnricher rootEnricher,
    ICampaignRollupMaintenance? maintenance = null,
    ISessionTurnClaimBeginStore? claimBegin = null,
    ISessionTurnBeginStore? sessionBegin = null,
    ISessionTurnClaimCoordinator? claims = null,
    ICovenantAuthoritySnapshotProvider? authoritySnapshot = null,
    ISessionTurnClaimLookup? lookup = null,
    IProtectedAssistantArtifactReader? protectedReplies = null,
    ISessionTurnClaimReplayStore? replayStore = null,
    ILogger<CampaignRollupTurnPreparer>? logger = null)
{
    internal static bool IsLiveEligible(ArcanumSettings configuration, PingRequest request, ArcanumInvocationContext invocation,
        bool requireProtectedToolPersistence = false) =>
        (invocation.Surface is ArcanumExecutionSurface.SessionBackedOperatorTurn
            || (requireProtectedToolPersistence && request.SessionId is null
                && invocation.Surface is ArcanumExecutionSurface.StatelessOperatorTurn))
        && invocation.ContextPolicy is CovenantContextPolicy.Default
        && invocation.Campaign is { } canonical && canonical != default
        && !InferenceContextBuilder.HasStatelessMessages(request)
        && (requireProtectedToolPersistence || configuration.Features.LongRest || (configuration.ResolveIntelligence().EnableCampaignRollups && canonical.IsCampaignBound));

    public async Task<Result<CampaignRollupTurnSnapshot?>> PrepareLiveAsync(
        PingRequest request,
        ArcanumInvocationContext invocation,
        bool streaming,
        CancellationToken cancellationToken,
        CovenantDigest? requestConfigurationDigest = null,
        CampaignPreparationProgress? progress = null,
        bool requireProtectedToolPersistence = false)
    {
        TurnIdempotencyRequestIdentity? accepted = TurnIdempotencyAmbient.RequestIdentity;

        ArcanumSettings preparationSettings = settings.CurrentValue;

        if (!IsLiveEligible(preparationSettings, request, invocation, requireProtectedToolPersistence))
        {
            if (accepted is not null)
            {
                if (lookup is null)
                {
                    return new Error(ErrorCodes.Covenant.Unavailable, "The accepted turn's existing ownership cannot be verified.");
                }

                Result<bool> existingClaim = await lookup.HasClaimAsync(
                    accepted.ClientTurnId, cancellationToken).ConfigureAwait(false);

                if (existingClaim.IsFailure)
                {
                    return existingClaim.Error;
                }

                if (existingClaim.Value)
                {
                    return new Error(ErrorCodes.Covenant.StaleSnapshot, "This accepted turn already owns a Campaign claim and its required context is no longer eligible.");
                }
            }

            return Result<CampaignRollupTurnSnapshot?>.Success(null);
        }

        CanonicalCampaignContext campaign = invocation.Campaign!.Value;

        // A stateless-classified prompt can still create a durable Session. Its claim owns only that creation;
        // it does not change the invocation's read/mutation authority or opt it into live maintenance.
        bool sessionMaintenance = invocation.Surface is ArcanumExecutionSurface.SessionBackedOperatorTurn;

        bool maintainRollups = sessionMaintenance && preparationSettings.ResolveIntelligence().EnableCampaignRollups && campaign.IsCampaignBound;

        bool maintainLongRest = sessionMaintenance && preparationSettings.Features.LongRest;

        LongRestConfigurationStamp? acceptedLongRestStamp = maintainLongRest ? longRestConfiguration.Capture().Stamp : null;

        if (claimBegin is null || claims is null
            || authoritySnapshot?.Current is not { } origin
            || !Guid.TryParse(origin.InstallationIdentity, out Guid installationId)
            || installationId == Guid.Empty)
        {
            return new Error(ErrorCodes.Covenant.OperatorAuthorityUnavailable, "Campaign summary maintenance has no established turn authority.");
        }

        CovenantDigest configurationDigest = ConfigurationDigest(preparationSettings);

        if (requestConfigurationDigest is { } scopedConfiguration && scopedConfiguration != configurationDigest)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "The request configuration changed before Campaign turn preparation.");
        }

        Guid clientTurnId = accepted?.ClientTurnId ?? Guid.NewGuid();

        CovenantDigest body = accepted?.AcceptedBodyDigest ?? new(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PingRequest)));

        SessionTurnSurface surface = accepted?.Surface ?? SessionTurnSurface.Intelligence;

        CovenantDigest requestDigest = CovenantDigests.SessionTurnRequest(new(
            surface,
            streaming ? CovenantProviderDispatchMode.Streaming : CovenantProviderDispatchMode.Buffered,
            clientTurnId, request.SessionId, invocation.ContextPolicy, accepted?.Route, body,
            request.CampaignId, null));

        Guid sessionId;

        if (request.SessionId is { } existing)
        {
            sessionId = existing;
        }
        else
        {
            SessionTurnClaim? previous = null;

            if (accepted is not null && lookup is not null)
            {
                Result<SessionTurnClaim?> found = await lookup.FindAsync(installationId, clientTurnId, cancellationToken).ConfigureAwait(false);

                if (found.IsFailure)
                {
                    return found.Error;
                }

                previous = found.Value;

                if (previous is not null && (previous.RequestDigest != requestDigest || previous.Surface != surface))
                {
                    return new Error(ErrorCodes.Security.IdempotencyConflict, "This accepted turn identity belongs to a different request.");
                }
            }

            if (previous is not null)
            {
                sessionId = previous.SessionId;
            }
            else
            {
                if (sessionBegin is null)
                {
                    return new Error(ErrorCodes.Covenant.Unavailable, "The Campaign turn-begin store is unavailable.");
                }

                Result<Guid> created = await sessionBegin.CreateBoundSessionAsync(campaign, request.Prompt, cancellationToken).ConfigureAwait(false);

                if (created.IsFailure)
                {
                    return created.Error;
                }

                sessionId = created.Value;
            }
        }

        Result<SessionTurnClaimInputSnapshot> input = await claimBegin.ReadClaimInputAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (input.IsFailure)
        {
            return input.Error;
        }

        Result<SessionTurnClaimLease> acquiredClaim = await claims.AcquireAsync(new(
            installationId, 0, clientTurnId, sessionId, surface, requestDigest,
            Dependencies(requestDigest, request, campaign, origin, configurationDigest),
            input.Value.HistoryWatermarkUtc, input.Value.HistoryRevision, input.Value.SensitivityRevision), cancellationToken).ConfigureAwait(false);

        if (acquiredClaim.IsFailure)
        {
            return acquiredClaim.Error;
        }

        SessionTurnClaimLease lease = acquiredClaim.Value;

        if (lease.IsExecutable && lease.Claim.State is SessionTurnClaimState.Begun)
        {
            return new Error(ErrorCodes.Hub.SessionTurnRestoredInterrupted, "This turn already began and has no replayable finalization; another provider call is refused.");
        }

        CovenantTurnLease? authority = null;

        bool transferred = false;

        SessionTurnClaimOutcome? terminalOutcome = null;

        Error RecordTerminal(Error error)
        {
            terminalOutcome = SessionTurnClaimOutcome.Discarded(error.Code,
                Primitives.ArcanumErrorMapper.ResolveStatusCode(error.Code), []);

            return error;
        }

        try
        {
            if (invocation.CanReadCovenant)
            {
                if (gate is null)
                {
                    return RecordTerminal(new Error(ErrorCodes.Covenant.Unavailable, "The authenticated turn lease is unavailable."));
                }

                Result<CovenantTurnLease> acquired = await gate.AcquireTurnAsync(campaign, cancellationToken).ConfigureAwait(false);

                if (acquired.IsFailure)
                {
                    return RecordTerminal(acquired.Error);
                }

                authority = acquired.Value;

                if (!invocation.ReadAuthorityEpoch!.Matches(authority.Snapshot))
                {
                    return RecordTerminal(new Error(ErrorCodes.Covenant.StaleSnapshot, "The authenticated Campaign read authority changed."));
                }
            }

            PingRequest boundRequest = request with { SessionId = sessionId };

            if (!lease.IsExecutable)
            {
                Result<PromptTurnResult> replay = await ReplayAsync(lease, authority, cancellationToken).ConfigureAwait(false);

                CampaignRollupTurnSnapshot replaySnapshot = new(null, campaign, boundRequest, store, settings, resolver, authority, lease, claims, configurationDigest,
                    requiresRollups: maintainRollups)
                {
                    Replay = replay,
                };

                transferred = true;

                return Result<CampaignRollupTurnSnapshot?>.Success(replaySnapshot);
            }

            if (maintainRollups && maintenance is null)
            {
                return RecordTerminal(new Error(ErrorCodes.Covenant.Unavailable, "Campaign summary maintenance is unavailable."));
            }

            LongRestConfigurationStamp? longRestStamp = null;

            if (maintainLongRest)
            {
                LongRestSweepConfiguration configuration = longRestConfiguration.Capture();

                if (!configuration.Enabled || !configuration.Stamp.IsValid
                    || acceptedLongRestStamp != configuration.Stamp)
                {
                    return RecordTerminal(new Error(ErrorCodes.Covenant.StaleSnapshot, "Long Rest changed before turn maintenance."));
                }

                longRestStamp = acceptedLongRestStamp;
            }

            Result CurrentConfiguration()
            {
                if (ConfigurationDigest(settings.CurrentValue) != configurationDigest
                    || (longRestStamp is not null && !longRestConfiguration.IsCurrent(longRestStamp)))
                {
                    return RecordTerminal(new Error(ErrorCodes.Covenant.StaleSnapshot, "The configuration accepted by this turn changed during maintenance."));
                }

                return Result.Success();
            }

            if (CurrentConfiguration() is { IsFailure: true } beforeMaintenance)
            {
                return RecordTerminal(beforeMaintenance.Error);
            }

            if (maintainLongRest)
            {
                Result<LongRunningOperationDto> clean = await longRest.RequestAsync(campaign, cancellationToken).ConfigureAwait(false);

                if (clean.IsFailure)
                {
                    // Clean maintenance is best-effort scheduling: its failure is the worker's and the operator's to resolve, not a reason
                    // to end the user's turn. Only a Long Rest error is skipped; a Covenant or configuration failure, and a cancellation
                    // (which is an exception, not a result), still end it. A busy clean scope is skipped like any other Long Rest refusal.
                    if (!LongRestFaultPolicy.IsLongRestError(clean.Error.Code))
                    {
                        return RecordTerminal(clean.Error);
                    }

                    ArcanumMetrics.LongRestCleanScheduleRefusedTotal.Add(1, new KeyValuePair<string, object?>("code", clean.Error.Code));

                    logger?.LogWarning("Clean Long Rest maintenance was not scheduled for this turn ({Code}); the turn proceeds.", clean.Error.Code);

                    // The configuration the turn accepted must still hold: a flip A to B to A never lets a refused schedule through.
                    if (CurrentConfiguration() is { IsFailure: true } afterRefusal)
                    {
                        return RecordTerminal(afterRefusal.Error);
                    }
                }

                // Clean work belongs to the existing independently owned background worker.
                // Only exact protected partitions must finish under this pending Session claim.
                if (authority is not null)
                {
                    CovenantDigest? after = null;

                    while (true)
                    {
                        if (CurrentConfiguration() is { IsFailure: true } currentConfiguration)
                        {
                            return RecordTerminal(currentConfiguration.Error);
                        }

                        Result live = await gate.ValidateTurnLeaseAsync(authority, cancellationToken).ConfigureAwait(false);

                        if (live.IsFailure)
                        {
                            return RecordTerminal(live.Error);
                        }

                        Result<CovenantDigest?> candidate = await longRestDiscovery.ReadNextProtectedPartitionAsync(
                            campaign, after, cancellationToken).ConfigureAwait(false);

                        if (candidate.IsFailure)
                        {
                            return RecordTerminal(candidate.Error);
                        }

                        if (candidate.Value is not { } partitionDigest)
                        {
                            break;
                        }

                        Result<LongRestSweepScope> partition = LongRestSweepScope.Create(new(
                            campaign.IsCampaignBound ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global,
                            campaign.CampaignId), partitionDigest);

                        if (partition.IsFailure)
                        {
                            return RecordTerminal(partition.Error);
                        }

                        // Clean-Only Busy Skip: a busy protected partition is not skipped. Only exact protected work finishing
                        // under the request keeps pending maintenance from ending with a protected partition unfinished.
                        // The request carries this turn's live authority, so a protected sweep parked with a fault that left its
                        // generation unable to complete is retired and replaced instead of failing every turn that needs the partition.
                        Result<LongRunningOperationDto> operation = await longRest.RequestAsync(
                            campaign, partition.Value, authority, cancellationToken).ConfigureAwait(false);

                        if (operation.IsFailure)
                        {
                            return RecordTerminal(operation.Error);
                        }

                        if (operation.Value.State is not LongRunningOperationState.Completed)
                        {
                            // Protected maintenance finishes under this turn's pending claim, so the caller is told once it is known to
                            // be coming (a partition whose sweep is already completed has none) and before the first paid work of it.
                            if (progress is not null)
                            {
                                await progress.SignalProtectedMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                            }

                            Result<LongRestMaintenanceCapability> prepared = await longRestMaintenance.CreateCapabilityAsync(
                                operation.Value.Id, invocation, lease, campaign, authority, cancellationToken).ConfigureAwait(false);

                            if (prepared.IsFailure)
                            {
                                return RecordTerminal(prepared.Error);
                            }

                            LongRestMaintenanceCapability capability = prepared.Value;

                            Result<LongRestSweepProgress> resumed;

                            try
                            {
                                resumed = await longRest.ResumeAsync(operation.Value.Id, capability, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                lease = capability.Claim;
                            }

                            if (resumed.IsFailure)
                            {
                                return RecordTerminal(resumed.Error);
                            }

                            if (resumed.Value.Phase is not LongRestSweepPhase.Completed || resumed.Value.DeferredReason is not null)
                            {
                                return RecordTerminal(ProtectedMaintenanceDeferred(operation.Value.Id, resumed.Value.DeferredReason));
                            }
                        }

                        after = partitionDigest;
                    }
                }
            }

            CampaignRollupArtifact? artifact = null;

            if (maintainRollups)
            {
                async Task<Result<CampaignRollupMaintenanceCapability?>> CapabilityAsync()
                {
                    if (CurrentConfiguration() is { IsFailure: true } current)
                    {
                        return RecordTerminal(current.Error);
                    }

                    if (authority is null)
                    {
                        return Result<CampaignRollupMaintenanceCapability?>.Success(null);
                    }

                    Result<CampaignRollupMaintenanceCapability> prepared = await maintenance!.CreateCapabilityAsync(
                        invocation, lease, campaign, authority, cancellationToken).ConfigureAwait(false);

                    return prepared.IsFailure ? prepared.Error : Result<CampaignRollupMaintenanceCapability?>.Success(prepared.Value);
                }

                Result<CampaignRollupMaintenanceCapability?> sessionCapability = await CapabilityAsync().ConfigureAwait(false);

                if (sessionCapability.IsFailure)
                {
                    return RecordTerminal(sessionCapability.Error);
                }

                Result<CampaignRollupMaintenanceResult> contributed = await maintenance!.ProcessSessionAsync(
                    sessionId, input.Value.HistoryRevision, sessionCapability.Value, cancellationToken).ConfigureAwait(false);

                if (contributed.IsFailure)
                {
                    return RecordTerminal(contributed.Error);
                }

                Result<CampaignRollupMaintenanceCapability?> campaignCapability = await CapabilityAsync().ConfigureAwait(false);

                if (campaignCapability.IsFailure)
                {
                    return RecordTerminal(campaignCapability.Error);
                }

                Result<CampaignRollupMaintenanceResult> folded = await maintenance!.ProcessCampaignAsync(
                    campaign.CampaignId!.Value, campaignCapability.Value, cancellationToken).ConfigureAwait(false);

                if (folded.IsFailure)
                {
                    return RecordTerminal(folded.Error);
                }

                if (CurrentConfiguration() is { IsFailure: true } beforeRead)
                {
                    return RecordTerminal(beforeRead.Error);
                }

                // Operational maintenance deferrals still reach this fresh, validating read.
                // Never substitute an older artifact when the current source or authority is invalid.
                Result<CampaignRollupArtifact?> current = await store.ReadCurrentAsync(campaign.CampaignId.Value, authority, cancellationToken).ConfigureAwait(false);

                if (current.IsFailure)
                {
                    return RecordTerminal(current.Error);
                }

                artifact = current.Value;
            }

            if (CurrentConfiguration() is { IsFailure: true } finalConfiguration)
            {
                return RecordTerminal(finalConfiguration.Error);
            }

            CampaignRollupTurnSnapshot snapshot = new(artifact, campaign, boundRequest, store, settings, resolver, authority, lease, claims,
                configurationDigest, maintainRollups, longRestStamp, longRestConfiguration, rootEnricher);

            transferred = true;

            return Result<CampaignRollupTurnSnapshot?>.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordTerminal(new Error(ErrorCodes.Hub.Error, "Turn preparation failed."));

            throw;
        }
        finally
        {
            if (!transferred)
            {
                try
                {
                    if (lease.IsExecutable)
                    {
                        _ = await claims.CompleteAsync(lease,
                            terminalOutcome ?? SessionTurnClaimOutcome.Discarded(ErrorCodes.Hub.SessionTurnRestoredInterrupted, 409, []), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (authority is not null)
                    {
                        await authority.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task<Result<PromptTurnResult>> ReplayAsync(SessionTurnClaimLease lease, CovenantTurnLease? authority, CancellationToken cancellationToken)
    {
        if (lease.Claim.State is not SessionTurnClaimState.Committed || lease.Claim.AssistantEntryId is not { } assistantId)
        {
            return new Error(lease.Claim.Outcome?.TerminalErrorCode ?? ErrorCodes.Hub.SessionTurnRestoredInterrupted,
                "This turn ended before a complete reply was produced.");
        }

        if (authority is not null && protectedReplies is not null)
        {
            Result<ProtectedAssistantArtifact> reply = await protectedReplies.ReadAsync(lease.Claim.SessionId, assistantId, authority, cancellationToken).ConfigureAwait(false);

            return reply.IsFailure ? reply.Error : Result<PromptTurnResult>.Success(new(reply.Value.Content, null));
        }

        if (replayStore is null)
        {
            return new Error(ErrorCodes.Covenant.Unavailable, "This terminal reply has no atomic clean read or protected read authority.");
        }

        Result<GrimoireEntryDto> clean = await replayStore.ReadCleanCommittedReplyAsync(lease, cancellationToken).ConfigureAwait(false);

        return clean.IsFailure ? clean.Error : Result<PromptTurnResult>.Success(new(clean.Value.Content, null));
    }

    internal static CovenantDigest ConfigurationDigest(ArcanumSettings configuration) => new(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(configuration, ArcanumJsonContext.Default.ArcanumSettings)));

    private static CovenantDigest Dependencies(CovenantDigest requestDigest, PingRequest request, CanonicalCampaignContext campaign, CovenantAuthoritySnapshot authority, CovenantDigest configurationDigest)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (!campaign.IsCampaignBound)
        {
            hash.AppendData("Arcanum.GlobalTurn.Dependencies.v1\0"u8);

            hash.AppendData(requestDigest.Bytes);

            hash.AppendData(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PingRequest)));

            Span<byte> globalCounters = stackalloc byte[16];

            BinaryPrimitives.WriteInt64BigEndian(globalCounters, authority.RuntimeAuthorityGeneration);

            BinaryPrimitives.WriteInt64BigEndian(globalCounters[8..], authority.AuthorityEpoch);

            hash.AppendData(globalCounters);

            hash.AppendData(configurationDigest.Bytes);

            return new(hash.GetHashAndReset());
        }

        hash.AppendData("Arcanum.CampaignTurn.Dependencies.v1\0"u8);

        hash.AppendData(requestDigest.Bytes);

        hash.AppendData(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PingRequest)));

        hash.AppendData(campaign.CampaignId!.Value.ToByteArray(bigEndian: true));

        Span<byte> counters = stackalloc byte[40];

        BinaryPrimitives.WriteInt64BigEndian(counters, authority.RuntimeAuthorityGeneration);

        BinaryPrimitives.WriteInt64BigEndian(counters[8..], authority.AuthorityEpoch);

        BinaryPrimitives.WriteInt64BigEndian(counters[16..], campaign.CampaignAvailabilityGeneration!.Value);

        BinaryPrimitives.WriteInt64BigEndian(counters[24..], campaign.PathIdentityPolicyVersion!.Value);

        BinaryPrimitives.WriteInt64BigEndian(counters[32..], campaign.PathIdentityRevision ?? 0);

        hash.AppendData(counters);

        hash.AppendData(campaign.RootIdentityDigest?.Bytes ?? new byte[32]);

        hash.AppendData(configurationDigest.Bytes);

        return new(hash.GetHashAndReset());
    }

    public async Task<Result<CampaignRollupTurnSnapshot?>> PreparePreviewAsync(
        PingRequest request,
        ArcanumInvocationContext invocation,
        CancellationToken cancellationToken,
        CovenantDigest? requestConfigurationDigest = null)
    {
        if (!settings.CurrentValue.ResolveIntelligence().EnableCampaignRollups
            || !invocation.IsOperatorSurface
            || invocation.ContextPolicy is CovenantContextPolicy.None
            || invocation.Campaign is not { IsCampaignBound: true } campaign)
        {
            return Result<CampaignRollupTurnSnapshot?>.Success(null);
        }

        CovenantDigest configurationDigest = ConfigurationDigest(settings.CurrentValue);

        if (requestConfigurationDigest is { } scopedConfiguration && scopedConfiguration != configurationDigest)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "The request configuration changed before Campaign summary inspection.");
        }

        CovenantTurnLease? authority = null;

        try
        {
            if (invocation.CanReadCovenant)
            {
                Result<CovenantTurnLease> acquired = await gate.AcquireTurnAsync(campaign, cancellationToken).ConfigureAwait(false);

                if (acquired.IsFailure)
                {
                    return acquired.Error;
                }

                authority = acquired.Value;

                if (!invocation.ReadAuthorityEpoch!.Matches(authority.Snapshot))
                {
                    return new Error(ErrorCodes.Covenant.StaleSnapshot, "The authenticated Campaign read authority changed.");
                }
            }

            Result<CampaignRollupArtifact?> current = await store.ReadCurrentAsync(
                campaign.CampaignId!.Value, authority, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }

            CampaignRollupTurnSnapshot snapshot = new(current.Value, campaign, request, store, settings, resolver, authority,
                frozenConfigurationDigest: configurationDigest);

            authority = null;

            return Result<CampaignRollupTurnSnapshot?>.Success(snapshot);
        }
        finally
        {
            if (authority is not null)
            {
                await authority.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Error ProtectedMaintenanceDeferred(Guid operationId, string? reason)
    {
        LongRestFaultClass fault = LongRestFaultClass.NeedsOperator;

        bool known = reason is not null && LongRestFaultPolicy.KnownCodes.TryGetValue(reason, out fault);

        string publicReason = reason is null ? "Incomplete" : known ? reason : "Unknown";

        string action = known && reason == LongRestParkCodes.AuthorizedRequestRequired
            ? "Make a fresh eligible Session request with Default context; operation retry alone cannot authorize protected work."
            : known && fault == LongRestFaultClass.ResumeLater
            ? "This is a transient condition. After it clears, make a fresh eligible Session request to revalidate protected work."
            : known && fault == LongRestFaultClass.RestartGeneration
            ? "Make a fresh eligible Session request to revalidate current sources and authority before starting a new generation."
            : "Review the stored operation outcome before deciding on another attempt; protected work requires a fresh eligible Session request.";

        return new Error(ErrorCodes.LongRest.Unavailable,
            $"Long Rest protected maintenance is deferred (reason {publicReason}, operation {operationId:D}). "
            + $"Inspect it with 'arcanum operation show {operationId:D}'. {action}");
    }
}
