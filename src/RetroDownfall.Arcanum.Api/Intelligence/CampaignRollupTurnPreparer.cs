using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>One frozen Campaign artifact, shared by every dispatch of a logical turn.</summary>
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
    CovenantDigest? frozenConfigurationDigest = null) : IAsyncDisposable
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

    public async Task<Result> ValidateAsync(CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.ResolveIntelligence().EnableCampaignRollups)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "Campaign summary injection was disabled during this turn.");
        }

        if (CampaignRollupTurnPreparer.ConfigurationDigest(settings.CurrentValue) != _configurationDigest)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "The configuration bound to this Campaign turn changed.");
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

        if (current.Value != Campaign)
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "This turn's canonical Campaign identity changed.");
        }

        return Artifact is null
            ? Result.Success()
            : await store.ValidateAsync(Artifact, Authority, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_begun && Replay is null && ClaimLease is { IsExecutable: true } lease && claims is not null)
            {
                _ = await claims.CompleteAsync(lease,
                    SessionTurnClaimOutcome.Discarded(ErrorCodes.Hub.SessionTurnRestoredInterrupted, 409, []),
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

/// <summary>The shared snapshot preparation seam for live inference and read-only inspection.</summary>
public sealed class CampaignRollupTurnPreparer(
    ICampaignRollupStore store,
    IOptionsMonitor<ArcanumSettings> settings,
    ICanonicalCampaignContextResolver resolver,
    ICovenantOperationGate gate,
    ICampaignRollupMaintenance? maintenance = null,
    ISessionTurnClaimBeginStore? claimBegin = null,
    ISessionTurnBeginStore? sessionBegin = null,
    ISessionTurnClaimCoordinator? claims = null,
    ICovenantAuthoritySnapshotProvider? authoritySnapshot = null,
    ISessionTurnClaimLookup? lookup = null,
    IProtectedAssistantArtifactReader? protectedReplies = null,
    ISessionTurnClaimReplayStore? replayStore = null)
{

    public async Task<Result<CampaignRollupTurnSnapshot?>> PrepareLiveAsync(
        PingRequest request,
        ArcanumInvocationContext invocation,
        bool streaming,
        CancellationToken cancellationToken,
        CovenantDigest? requestConfigurationDigest = null)
    {
        TurnIdempotencyRequestIdentity? accepted = TurnIdempotencyAmbient.RequestIdentity;

        if (!settings.CurrentValue.ResolveIntelligence().EnableCampaignRollups
            || invocation.Surface is not ArcanumExecutionSurface.SessionBackedOperatorTurn
            || invocation.ContextPolicy is CovenantContextPolicy.None
            || invocation.Campaign is not { IsCampaignBound: true } campaign
            || InferenceContextBuilder.HasStatelessMessages(request))
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

        if (maintenance is null || claimBegin is null || claims is null
            || authoritySnapshot?.Current is not { } origin
            || !Guid.TryParse(origin.InstallationIdentity, out Guid installationId)
            || installationId == Guid.Empty)
        {
            return new Error(ErrorCodes.Covenant.OperatorAuthorityUnavailable, "Campaign summary maintenance has no established turn authority.");
        }

        CovenantDigest configurationDigest = ConfigurationDigest(settings.CurrentValue);

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

            PingRequest boundRequest = request with { SessionId = sessionId };

            if (!lease.IsExecutable)
            {
                Result<PromptTurnResult> replay = await ReplayAsync(lease, authority, cancellationToken).ConfigureAwait(false);

                CampaignRollupTurnSnapshot replaySnapshot = new(null, campaign, boundRequest, store, settings, resolver, authority, lease, claims, configurationDigest)
                {
                    Replay = replay,
                };

                transferred = true;

                return Result<CampaignRollupTurnSnapshot?>.Success(replaySnapshot);
            }

            async Task<Result<CampaignRollupMaintenanceCapability?>> CapabilityAsync()
            {
                if (ConfigurationDigest(settings.CurrentValue) != configurationDigest)
                {
                    return new Error(ErrorCodes.Covenant.StaleSnapshot, "The configuration accepted by this Campaign claim changed before maintenance.");
                }

                if (authority is null)
                {
                    return Result<CampaignRollupMaintenanceCapability?>.Success(null);
                }

                Result<CampaignRollupMaintenanceCapability> prepared = await maintenance.CreateCapabilityAsync(
                    invocation, lease, campaign, authority, cancellationToken).ConfigureAwait(false);

                return prepared.IsFailure ? prepared.Error : Result<CampaignRollupMaintenanceCapability?>.Success(prepared.Value);
            }

            Result<CampaignRollupMaintenanceCapability?> sessionCapability = await CapabilityAsync().ConfigureAwait(false);

            if (sessionCapability.IsFailure)
            {
                return sessionCapability.Error;
            }

            Result<CampaignRollupMaintenanceResult> contributed = await maintenance.ProcessSessionAsync(
                sessionId, input.Value.HistoryRevision, sessionCapability.Value, cancellationToken).ConfigureAwait(false);

            if (contributed.IsFailure)
            {
                return contributed.Error;
            }

            Result<CampaignRollupMaintenanceCapability?> campaignCapability = await CapabilityAsync().ConfigureAwait(false);

            if (campaignCapability.IsFailure)
            {
                return campaignCapability.Error;
            }

            Result<CampaignRollupMaintenanceResult> folded = await maintenance.ProcessCampaignAsync(
                campaign.CampaignId!.Value, campaignCapability.Value, cancellationToken).ConfigureAwait(false);

            if (folded.IsFailure)
            {
                return folded.Error;
            }

            if (ConfigurationDigest(settings.CurrentValue) != configurationDigest)
            {
                return new Error(ErrorCodes.Covenant.StaleSnapshot, "The configuration accepted by this Campaign claim changed during maintenance.");
            }

            Result<CampaignRollupArtifact?> current = await store.ReadCurrentAsync(campaign.CampaignId.Value, authority, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }

            CampaignRollupTurnSnapshot snapshot = new(current.Value, campaign, boundRequest, store, settings, resolver, authority, lease, claims, configurationDigest);

            transferred = true;

            return Result<CampaignRollupTurnSnapshot?>.Success(snapshot);
        }
        finally
        {
            if (!transferred)
            {
                if (lease.IsExecutable)
                {
                    _ = await claims.CompleteAsync(lease,
                        SessionTurnClaimOutcome.Discarded(ErrorCodes.Hub.SessionTurnRestoredInterrupted, 409, []), CancellationToken.None).ConfigureAwait(false);
                }

                if (authority is not null)
                {
                    await authority.DisposeAsync().ConfigureAwait(false);
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

}
