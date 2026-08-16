using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The caller-side adapter for a Covenant operation the caller names itself.
/// </summary>
/// <remarks>
/// Every assertion is about what the adapter does <em>not</em> do. The durable identity row, the
/// digest comparison, the replay-without-a-second-lease rule, and the conflict refusal all belong to
/// the shipped store and coordinator; a second implementation of any of them here would be a second
/// opinion about whether two requests are the same request (§10.16).
/// </remarks>
public sealed class CovenantRequestedOperationStarterTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Requested_start_delegates_the_exact_all_present_identity_triplet()
    {

        RecordingOperationCoordinator coordinator = new();

        CovenantRequestedOperationStarter starter = new(coordinator);

        Guid requestedOperationId = Guid.Parse("77777777-7777-4777-8777-777777777777");

        _ = await starter.StartRequestedAsync(
            LongRunningOperationKinds.CovenantFamilyReinitialize,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            "Reinitializing.",
            DateTimeOffset.UnixEpoch,
            requestedOperationId,
            CovenantOperationGateFixture.Digest(1),
            CovenantOperationGateFixture.Digest(2),
            "owner",
            TimeSpan.FromMinutes(30),
            Token);

        Assert.NotNull(coordinator.LastIdentity);

        Assert.Equal(requestedOperationId, coordinator.LastIdentity.RequestedOperationId);

        Assert.Equal(CovenantOperationGateFixture.Digest(1), coordinator.LastIdentity.ApplyRequestDigest);

        Assert.Equal(CovenantOperationGateFixture.Digest(2), coordinator.LastIdentity.EffectDigest);

        Assert.NotNull(coordinator.LastRequest);

        Assert.Equal(LongRunningOperationKinds.CovenantFamilyReinitialize, coordinator.LastRequest.Kind);

        // Every optional correlation field stays null: a reinitialize owns no Session, run, inference,
        // reservation, or claim, and a fabricated link would give recovery a relationship to reason
        // about that does not exist.
        Assert.Null(coordinator.LastRequest.SessionId);

        Assert.Null(coordinator.LastRequest.RunId);

        Assert.Null(coordinator.LastRequest.InferenceRunId);

        Assert.Null(coordinator.LastRequest.BudgetReservationId);

        Assert.Null(coordinator.LastRequest.IdempotencyClaimId);

        Assert.Null(coordinator.LastRequest.RootOperationId);

        Assert.Null(coordinator.LastRequest.ParentOperationId);

    }

    [Fact]
    public async Task Same_identity_replay_and_digest_conflict_are_returned_unchanged()
    {

        RecordingOperationCoordinator coordinator = new()
        {
            Outcome = LongRunningOperationRequestIdentityOutcome.Replayed,
        };

        CovenantRequestedOperationStarter starter = new(coordinator);

        Result<LongRunningOperationRequestIdentityResult> replayed = await StartAsync(starter);

        Assert.True(replayed.IsSuccess);

        Assert.Equal(LongRunningOperationRequestIdentityOutcome.Replayed, replayed.Value.Outcome);

        coordinator.Failure = new Error(ErrorCodes.Security.IdempotencyConflict, "different effect");

        Result<LongRunningOperationRequestIdentityResult> conflict = await StartAsync(starter);

        Assert.True(conflict.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflict.Error.Code);

        Assert.Equal(2, coordinator.Calls);

    }

    [Fact]
    public void The_adapter_owns_no_store_codec_or_public_dto_dependency()
    {

        IReadOnlyList<Type> dependencies =
        [
            .. typeof(CovenantRequestedOperationStarter)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(static constructor => constructor.GetParameters())
                .Select(static parameter => parameter.ParameterType),
        ];

        Assert.Equal([typeof(ILongRunningOperationCoordinator)], dependencies);

        Assert.DoesNotContain(
            typeof(CovenantRequestedOperationStarter)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .SelectMany(static method => method.GetParameters())
                .Select(static parameter => parameter.ParameterType),
            type => type == typeof(LongRunningOperationDto));

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unnamed_kind_summary_or_owner_is_refused_before_the_coordinator_is_called(string blank)
    {

        RecordingOperationCoordinator coordinator = new();

        CovenantRequestedOperationStarter starter = new(coordinator);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => starter.StartRequestedAsync(
            blank,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            "summary",
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            CovenantOperationGateFixture.Digest(1),
            CovenantOperationGateFixture.Digest(2),
            "owner",
            TimeSpan.FromMinutes(1),
            Token));

        Assert.Equal(0, coordinator.Calls);

    }

    private static Task<Result<LongRunningOperationRequestIdentityResult>> StartAsync(
        CovenantRequestedOperationStarter starter) =>
        starter.StartRequestedAsync(
            LongRunningOperationKinds.CovenantFamilyReinitialize,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            "Reinitializing.",
            DateTimeOffset.UnixEpoch,
            Guid.Parse("77777777-7777-4777-8777-777777777777"),
            CovenantOperationGateFixture.Digest(1),
            CovenantOperationGateFixture.Digest(2),
            "owner",
            TimeSpan.FromMinutes(30),
            Token);

    private sealed class RecordingOperationCoordinator : ILongRunningOperationCoordinator
    {

        internal LongRunningOperationCreateRequest? LastRequest { get; private set; }

        internal LongRunningOperationRequestIdentity? LastIdentity { get; private set; }

        internal LongRunningOperationRequestIdentityOutcome Outcome { get; set; } =
            LongRunningOperationRequestIdentityOutcome.Created;

        internal Error? Failure { get; set; }

        internal int Calls { get; private set; }

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A named Covenant operation never takes the unnamed arm.");

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {

            Calls++;

            LastRequest = request;

            LastIdentity = identity;

            return Task.FromResult(
                Failure is { } error
                    ? Result<LongRunningOperationRequestIdentityResult>.Failure(error)
                    : Result<LongRunningOperationRequestIdentityResult>.Success(
                        new LongRunningOperationRequestIdentityResult(Outcome, null)));

        }

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken) => Task.FromResult(true);

    }

}
