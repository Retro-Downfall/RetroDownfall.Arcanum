using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignRollupTurnSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_keyed_inference_checks_only_local_ownership_when_runtime_authority_is_unavailable(bool alreadyOwned)
    {
        ProbeStore store = new();

        DisabledClaimLookup lookup = new(alreadyOwned);

        CampaignRollupTurnPreparer preparer = new(
            store, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new Resolver(), null!, authoritySnapshot: new UnavailableAuthority(), lookup: lookup);

        TurnIdempotencyAmbient.PublishIdentity(new string('B', 64), new string('C', 64));

        try
        {
            Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
                Request(), ArcanumInvocationContext.None, false, CancellationToken.None);

            if (alreadyOwned)
            {
                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
            }
            else
            {
                Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                Assert.Null(result.Value);
            }

            Assert.Equal(1, lookup.OwnershipChecks);

            Assert.Equal(0, store.Reads);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    private sealed class UnavailableAuthority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current => null;
    }

    private sealed class DisabledClaimLookup(bool alreadyOwned) : ISessionTurnClaimLookup
    {
        public int OwnershipChecks { get; private set; }

        public ValueTask<Result<bool>> HasClaimAsync(Guid clientTurnId, CancellationToken cancellationToken)
        {
            OwnershipChecks++;

            Assert.NotEqual(Guid.Empty, clientTurnId);

            return ValueTask.FromResult(Result<bool>.Success(alreadyOwned));
        }

        public ValueTask<Result<SessionTurnClaim?>> FindAsync(Guid installationId, Guid clientTurnId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Disabled inference must not request a Covenant origin or a claim payload.");
    }


    internal static readonly Guid CampaignId = Guid.NewGuid();

    internal static readonly CanonicalCampaignContext Campaign = CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(CampaignId), 1, 1, null, null);

    [Fact]
    public async Task Disabled_and_no_context_preview_perform_no_Campaign_lookup()
    {
        ArcanumSettings settings = new();

        ProbeStore store = new();

        CampaignRollupTurnPreparer preparer = Preparer(store, settings);

        Assert.Null((await preparer.PreparePreviewAsync(Request(), Inspection(), CancellationToken.None)).Value);

        settings.Features.CampaignRollups = true;

        Assert.Null((await preparer.PreparePreviewAsync(Request(), Inspection(CovenantContextPolicy.None), CancellationToken.None)).Value);

        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Preview_reads_one_frozen_artifact_and_a_later_publication_does_not_replace_it()
    {
        ArcanumSettings settings = Enabled();

        CampaignRollupArtifact original = Artifact("Use stable logical keys.", ContentSensitivity.None);

        ProbeStore store = new() { Current = original };

        CampaignRollupTurnSnapshot snapshot = (await Preparer(store, settings)
            .PreparePreviewAsync(Request(), Inspection(), CancellationToken.None)).Value!;

        Assert.NotNull(snapshot);

        store.Current = Artifact("A later publication.", ContentSensitivity.None);

        Assert.True((await snapshot.ValidateAsync(CancellationToken.None)).IsSuccess);

        Assert.Same(original, snapshot.Artifact);

        Assert.Same(original, store.LastValidated);

        Assert.Equal(1, store.Reads);

        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task Disabling_the_feature_or_invalidating_the_bound_source_refuses_the_next_dispatch()
    {
        ArcanumSettings settings = Enabled();

        ProbeStore store = new() { Current = Artifact("Remember the decision.", ContentSensitivity.None) };

        CampaignRollupTurnSnapshot snapshot = (await Preparer(store, settings)
            .PreparePreviewAsync(Request(), Inspection(), CancellationToken.None)).Value!;

        Assert.NotNull(snapshot);

        settings.Features.CampaignRollups = false;

        Assert.True((await snapshot.ValidateAsync(CancellationToken.None)).IsFailure);

        settings.Features.CampaignRollups = true;

        store.Validation = new Error(ErrorCodes.Covenant.StaleSnapshot, "The consumed source was erased.");

        Assert.True((await snapshot.ValidateAsync(CancellationToken.None)).IsFailure);

        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task A_live_claim_refusal_reads_no_summary_and_performs_no_maintenance()
    {
        ProbeStore store = new();

        RefusingClaims claims = new();

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(Enabled()),
            new Resolver(), null!, new RefusingMaintenance(), new InputStore(), null, claims, new Authority());

        Result<CampaignRollupTurnSnapshot?> prepared = await preparer.PrepareLiveAsync(
            Request(), invocation, false, CancellationToken.None);

        Assert.True(prepared.IsFailure);

        Assert.Equal(ErrorCodes.Hub.SessionTurnBusy, prepared.Error.Code);

        Assert.Equal(1, claims.Acquisitions);

        Assert.Equal(0, store.Reads);
    }

    internal sealed class Authority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; } = new(1, CampaignId.ToString("D"), 1, 1, 1, CovenantHostToolsState.Clean, null);
    }

    [Fact]
    public async Task A_live_turn_maintains_pre_request_history_before_reading_its_frozen_Campaign_context()
    {
        CampaignRollupArtifact artifact = Artifact("The earlier Session established stable keys.", ContentSensitivity.None);

        ProbeStore store = new() { Current = artifact };

        AcceptingClaims claims = new();

        PingRequest request = Request();

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(Enabled()),
            new Resolver(), null!, new RecordingMaintenance(store.Actions), new InputStore(), null, claims, new Authority());

        Result<CampaignRollupTurnSnapshot?> prepared = await preparer.PrepareLiveAsync(request, invocation, true, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        CampaignRollupTurnSnapshot snapshot = prepared.Value!;

        Assert.NotNull(snapshot);

        Assert.Equal(new[] { "session:2", "campaign", "read" }, store.Actions);

        Assert.Same(artifact, snapshot.Artifact);

        Assert.Equal(request.SessionId, snapshot.SessionId);

        Assert.NotNull(snapshot.ClaimLease);

        Assert.Null(snapshot.Replay);


        await snapshot.DisposeAsync();

        Assert.Equal(1, claims.Completions);
    }

    internal sealed class AcceptingClaims : ISessionTurnClaimCoordinator
    {
        public int Completions { get; private set; }


        public ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken cancellationToken)
        {
            Guid boot = Guid.NewGuid();

            SessionTurnClaim claim = new(Guid.NewGuid(), request.OriginInstallationId, request.OriginRestoreEpoch,
                request.ClientTurnId, request.SessionId, request.Surface, request.RequestDigest, request.DependencyDigest,
                SessionTurnClaimState.PendingMaintenance, request.PreRequestHistoryWatermarkUtc, request.PreRequestHistoryRevision,
                request.InputSensitivityRevision, request.InputSensitivityRevision, Guid.NewGuid(), null, null, boot,
                0, 0, null, DateTimeOffset.UtcNow);

            return ValueTask.FromResult(Result<SessionTurnClaimLease>.Success(
                new(claim, SessionTurnClaimDisposition.Created, Guid.NewGuid(), Guid.NewGuid(), boot, DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken cancellationToken)
        {
            Completions++;

            return ValueTask.FromResult(Result<SessionTurnClaim>.Success(lease.Claim with { State = outcome.State, Outcome = outcome }));
        }
    }

    private sealed class RecordingMaintenance(List<string> actions) : ICampaignRollupMaintenance
    {
        public Task<Result<CampaignRollupMaintenanceCapability>> CreateCapabilityAsync(ArcanumInvocationContext invocation, SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CovenantTurnLease maintenanceTurnLease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessSessionAsync(Guid sessionId, long? upperSequence, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken)
        {
            Assert.Null(capability);

            actions.Add("session:" + upperSequence);

            return Task.FromResult(Result<CampaignRollupMaintenanceResult>.Success(new(1, false, null)));
        }

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessCampaignAsync(Guid campaignId, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken)
        {
            Assert.Null(capability);

            Assert.Equal(CampaignId, campaignId);

            actions.Add("campaign");

            return Task.FromResult(Result<CampaignRollupMaintenanceResult>.Success(new(1, false, null)));
        }
    }

    internal sealed class InputStore : ISessionTurnClaimBeginStore
    {
        public ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(Guid sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionTurnClaimInputSnapshot>.Success(new(sessionId, 2, null, 0)));

        public ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, string prompt, string model, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RefusingClaims : ISessionTurnClaimCoordinator
    {
        public int Acquisitions { get; private set; }

        public ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken cancellationToken)
        {
            Acquisitions++;

            return ValueTask.FromResult(Result<SessionTurnClaimLease>.Failure(new Error(ErrorCodes.Hub.SessionTurnBusy, "Another turn owns the Session.")));
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class RefusingMaintenance : ICampaignRollupMaintenance
    {
        public Task<Result<CampaignRollupMaintenanceCapability>> CreateCapabilityAsync(ArcanumInvocationContext invocation, SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CovenantTurnLease maintenanceTurnLease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessSessionAsync(Guid sessionId, long? upperSequence, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessCampaignAsync(Guid campaignId, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static CampaignRollupTurnPreparer Preparer(ProbeStore store, ArcanumSettings settings) =>
        new(store, new TestOptionsMonitor<ArcanumSettings>(settings), new Resolver(), null!);

    private static ArcanumSettings Enabled()
    {
        ArcanumSettings settings = new();

        settings.Features.CampaignRollups = true;

        return settings;
    }

    private static PingRequest Request() => new("Continue our work.", SessionId: Guid.NewGuid());

    private static ArcanumInvocationContext Inspection(CovenantContextPolicy policy = CovenantContextPolicy.Default) =>
        ArcanumInvocationContext.Create(ArcanumExecutionSurface.ContextInspection, Campaign,
            InvocationAttendance.Attended, policy, ToolPolicy.NoTools, null).Value;

    internal static CampaignRollupArtifact Artifact(string content, ContentSensitivity sensitivity)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact(
            sensitivity is ContentSensitivity.None ? [] : [CampaignId]);

        CovenantDigest digest = new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

        CovenantDigest sensitivityDigest = CovenantDigests.Sensitivity(new SensitivityDigestInput(
            sensitivity, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits));

        return new(Guid.NewGuid(), CampaignId, null, 1, content, digest, sensitivity, provenance,
            sensitivityDigest, 1, digest, 0, 1);
    }

    internal sealed class Resolver : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(Campaign));
    }

    internal sealed class ProbeStore : ICampaignRollupStore
    {
        public List<string> Actions { get; } = [];
        public int Reads { get; private set; }

        public CampaignRollupArtifact? Current { get; set; }

        public CampaignRollupArtifact? LastValidated { get; private set; }

        public Result Validation { get; set; } = Result.Success();

        public Task<Result<CampaignRollupArtifact?>> ReadCurrentAsync(Guid campaignId, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
        {
            Reads++;

            Actions.Add("read");

            return Task.FromResult(Result<CampaignRollupArtifact?>.Success(Current));
        }

        public Task<Result> ValidateAsync(CampaignRollupArtifact artifact, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
        {
            LastValidated = artifact;

            return Task.FromResult(Validation);
        }

        public Task<Result<CampaignContributionInput?>> PrepareContributionAsync(Guid sessionId, long? throughSequence, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupInput?>> PrepareRollupAsync(Guid campaignId, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupArtifact>> PublishContributionAsync(CampaignContributionInput input, string content, CampaignMaintenancePublication? maintenancePublication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupArtifact>> PublishRollupAsync(CampaignRollupInput input, string content, CampaignMaintenancePublication? maintenancePublication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupStatus>> ReadStatusAsync(Guid campaignId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindPendingContributionsAsync(DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindPendingContributionsForCampaignAsync(Guid campaignId, DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
