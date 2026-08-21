using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The borrowed erasure capability: what it accepts, what it compares, and what it refuses to own.
/// </summary>
public sealed class CovenantArtifactErasureAuthorityTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData(CovenantArtifactErasureAuthorityKind.Ordinary, 1)]
    [InlineData(CovenantArtifactErasureAuthorityKind.Exclusive, 2)]
    public void Erasure_authority_kind_codes_are_literal_and_exhaustive(
        CovenantArtifactErasureAuthorityKind kind,
        byte code)
    {

        Assert.Equal(code, (byte)kind);

        Assert.Equal(2, Enum.GetValues<CovenantArtifactErasureAuthorityKind>().Length);

    }

    [Theory]
    [InlineData(CovenantErasureBlocker.None, 0)]
    [InlineData(CovenantErasureBlocker.ManualOwnershipMismatch, 1)]
    [InlineData(CovenantErasureBlocker.AuthorityStale, 2)]
    [InlineData(CovenantErasureBlocker.IntegrityFailure, 3)]
    [InlineData(CovenantErasureBlocker.StorageUnavailable, 4)]
    public void Erasure_blocker_codes_are_literal_and_exhaustive(CovenantErasureBlocker blocker, byte code)
    {

        Assert.Equal(code, (byte)blocker);

        Assert.Equal(5, Enum.GetValues<CovenantErasureBlocker>().Length);

    }

    [Fact]
    public async Task Ordinary_authority_compares_only_shared_epoch_identity_and_key_facts()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        OperatorAuthorityContext context = CovenantErasureAuthorityFixture.OperatorContext(authority);

        Result<CovenantArtifactErasureAuthority> borrowed = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            context,
            CovenantErasureAuthorityFixture.Issuer(authority));

        Assert.True(borrowed.IsSuccess);

        Assert.Equal(CovenantArtifactErasureAuthorityKind.Ordinary, borrowed.Value.Kind);

        Assert.Null(borrowed.Value.ExclusiveOperation);

        Assert.Equal(lease.Snapshot.AuthorityEpoch, context.AuthorityEpoch);

        // The snapshot exposes neither an installation identity nor a master-key version, so those two
        // facts are deliberately uncompared rather than aliased onto some other generation field.
        Assert.Null(typeof(CovenantOperationLeaseSnapshot).GetProperty("InstallationIdentity"));

        Assert.Null(typeof(CovenantOperationLeaseSnapshot).GetProperty("MasterKeyVersion"));

    }

    [Fact]
    public async Task Ordinary_authority_requires_the_purge_requirement_and_a_matching_authority_epoch()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        Result<CovenantArtifactErasureAuthority> wrongRequirement = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(authority, CovenantAuthorityRequirement.ProtectedRead),
            CovenantErasureAuthorityFixture.Issuer(authority));

        Assert.True(wrongRequirement.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, wrongRequirement.Error.Code);

        CovenantAuthoritySnapshot current = authority.Current!;

        OperatorAuthorityContext differentEpoch = OperatorAuthorityContext.CreateForTests(
            CovenantAuthorityRequirement.SensitivityRetentionPurge,
            Guid.Parse(current.InstallationIdentity),
            lease.Snapshot.RuntimeAuthorityGeneration,
            current.AuthorityEpoch + 1,
            current.MasterKeyVersion);

        Result<CovenantArtifactErasureAuthority> staleEpoch = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            differentEpoch,
            CovenantErasureAuthorityFixture.Issuer(authority));

        Assert.True(staleEpoch.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, staleEpoch.Error.Code);

    }

    [Fact]
    public void Ordinary_authority_rejects_equal_durable_epochs_from_different_runtime_generations()
    {

        CovenantWriteLease lease = new(new FixedRegistration(runtimeAuthorityGeneration: 1));

        OperatorAuthorityContext context = OperatorAuthorityContext.CreateForTests(
            CovenantAuthorityRequirement.SensitivityRetentionPurge,
            Guid.Parse("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"),
            runtimeAuthorityGeneration: 2,
            authorityEpoch: 11,
            masterKeyVersion: 4);

        Result<CovenantArtifactErasureAuthority> result = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            context,
            new UnreachableIssuer());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

    }

    [Fact]
    public async Task Ordinary_authority_revalidates_the_operator_context_before_its_lease()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        RevocableOperatorAuthorityIssuer issuer = new(authority);

        CovenantArtifactErasureAuthority borrowed = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(authority),
            issuer).Value;

        Assert.True((await borrowed.RevalidateAsync(Token)).IsSuccess);

        Assert.Equal(1, issuer.RevalidationCount);

        issuer.RevalidationFails = true;

        Result refused = await borrowed.RevalidateAsync(Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
    [InlineData(CovenantExclusiveOperation.CovenantReset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
    public async Task Exclusive_authority_accepts_only_the_three_erasure_operations(
        CovenantExclusiveOperation operation)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(operation),
            Token)).Value;

        Result<CovenantArtifactErasureAuthority> borrowed =
            CovenantArtifactErasureAuthority.ForExclusive(lease, operation);

        Assert.True(borrowed.IsSuccess);

        Assert.Equal(CovenantArtifactErasureAuthorityKind.Exclusive, borrowed.Value.Kind);

        Assert.Equal(operation, borrowed.Value.ExclusiveOperation);

        Assert.Contains(operation, CovenantArtifactErasureAuthority.ErasureOperations);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.SchemaRepair)]
    [InlineData(CovenantExclusiveOperation.BackupRestore)]
    public async Task Exclusive_authority_rejects_every_non_erasure_global_operation(
        CovenantExclusiveOperation operation)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(operation),
            Token)).Value;

        Result<CovenantArtifactErasureAuthority> borrowed =
            CovenantArtifactErasureAuthority.ForExclusive(lease, operation);

        Assert.True(borrowed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, borrowed.Error.Code);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CampaignPathMutation)]
    [InlineData(CovenantExclusiveOperation.CampaignDelete)]
    [InlineData(CovenantExclusiveOperation.ProtectedSessionTransfer)]
    public void Exclusive_authority_rejects_every_scoped_operation_without_touching_a_lease(
        CovenantExclusiveOperation operation)
    {

        Assert.DoesNotContain(operation, CovenantArtifactErasureAuthority.ErasureOperations);

    }

    [Fact]
    public async Task Exclusive_authority_rejects_a_lease_registered_under_a_different_owner()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Result<CovenantArtifactErasureAuthority> borrowed = CovenantArtifactErasureAuthority.ForExclusive(
            lease,
            CovenantExclusiveOperation.CovenantFamilyReinitialize);

        Assert.True(borrowed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, borrowed.Error.Code);

    }

    [Fact]
    public async Task Global_only_coverage_never_covers_a_campaign_owner()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantWriteLease global = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        CovenantArtifactErasureAuthority borrowed = CovenantArtifactErasureAuthority.ForOrdinary(
            global,
            CovenantErasureAuthorityFixture.OperatorContext(authority),
            CovenantErasureAuthorityFixture.Issuer(authority)).Value;

        Assert.True(borrowed.Covers(CovenantOperationScope.Global));

        Assert.False(borrowed.Covers(CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne)));

        Assert.False(borrowed.Covers(default));

    }

    [Fact]
    public async Task Campaign_coverage_never_covers_another_campaign_or_global()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantWriteLease scoped = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        CovenantArtifactErasureAuthority borrowed = CovenantArtifactErasureAuthority.ForOrdinary(
            scoped,
            CovenantErasureAuthorityFixture.OperatorContext(authority),
            CovenantErasureAuthorityFixture.Issuer(authority)).Value;

        Assert.True(borrowed.Covers(CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne)));

        Assert.False(borrowed.Covers(CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo)));

        Assert.False(borrowed.Covers(CovenantOperationScope.Global));

    }

    [Fact]
    public async Task Installation_coverage_covers_every_owner_scope()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority borrowed =
            CovenantArtifactErasureAuthority.ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize).Value;

        Assert.Equal(CovenantLeaseCoverage.Installation, borrowed.Snapshot.Coverage);

        Assert.True(borrowed.Covers(CovenantOperationScope.Global));

        Assert.True(borrowed.Covers(CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo)));

    }

    /// <summary>
    /// The authority borrows a capability; it never owns one. A member that could complete or dispose
    /// a lease would let a kernel reopen admission the coordinator is still holding closed.
    /// </summary>
    [Fact]
    public void Erasure_authority_exposes_no_way_to_acquire_complete_or_dispose_a_lease()
    {

        IReadOnlyList<string> members =
        [
            .. typeof(CovenantArtifactErasureAuthority)
                .GetMembers()
                .Select(static member => member.Name),
        ];

        Assert.DoesNotContain("CompleteAsync", members);

        Assert.DoesNotContain("DisposeAsync", members);

        Assert.DoesNotContain("Dispose", members);

        Assert.DoesNotContain("Lease", members);

        Assert.Empty(typeof(CovenantArtifactErasureAuthority).GetConstructors());

    }

    [Fact]
    public void Progress_folding_is_checked_and_keeps_the_first_blocker()
    {

        CovenantArtifactErasureProgress blocked = new(2, 1, 1, CovenantErasureBlocker.ManualOwnershipMismatch);

        CovenantArtifactErasureProgress clean = new(3, 3, 0, CovenantErasureBlocker.None);

        CovenantArtifactErasureProgress folded = blocked.Add(clean);

        Assert.Equal(5UL, folded.ExaminedCount);

        Assert.Equal(4UL, folded.ErasedCount);

        Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, folded.Blocker);

        Assert.Equal(
            CovenantErasureBlocker.IntegrityFailure,
            clean.Add(new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.IntegrityFailure)).Blocker);

        _ = Assert.Throws<OverflowException>(() =>
            new CovenantArtifactErasureProgress(ulong.MaxValue, 0, 0, CovenantErasureBlocker.None)
                .Add(new CovenantArtifactErasureProgress(1, 0, 0, CovenantErasureBlocker.None)));

    }

    private sealed class FixedRegistration(long runtimeAuthorityGeneration) : ICovenantLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            runtimeAuthorityGeneration,
            CovenantLeaseKind.Write,
            CovenantLeaseCoverage.Scoped,
            CovenantOperationScope.Global,
            CovenantOperationGateFixture.DatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The runtime mismatch must be refused before revalidation.");

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    }

    private sealed class UnreachableIssuer : IOperatorAuthorityContextIssuer
    {

        public Result<OperatorAuthorityContext> Issue(CovenantAuthorityRequirement requirement) =>
            throw new InvalidOperationException("The runtime mismatch must be refused before issuing authority.");

        public Result<CovenantReadAuthorityEpoch> IssueReadEpoch() =>
            throw new InvalidOperationException("The runtime mismatch must be refused before issuing a read epoch.");

        public Result Revalidate(OperatorAuthorityContext context) =>
            throw new InvalidOperationException("The runtime mismatch must be refused before context revalidation.");

    }

}
