using System.Collections.Immutable;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_receipt_and_item_cursor_commit_or_rollback_together(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(DBNull.Value, await fixture.H.ScalarAsync("SELECT CommittedItemCursor FROM long_rest_sweeps;"));

        await fixture.H.ExecuteAsync("CREATE TEMP TRIGGER fail_sweep_cursor BEFORE UPDATE OF CommittedItemCursor ON long_rest_sweeps BEGIN SELECT RAISE(ABORT, 'cursor failure'); END;");

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        await fixture.H.ExecuteAsync("DROP TRIGGER fail_sweep_cursor;");

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.NotNull(committed.CommittedItemCursor);

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(protect ? 2L : 1L, await fixture.H.ScalarAsync("SELECT PolicyVersion FROM long_rest_receipts;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2 AND ReceiptId IS NOT NULL;"));

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        LongRestSweep replay = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.Equal(committed, replay);

        Assert.Equal(changes, (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Accepted_adjudication_survives_fresh_store_and_real_boot_without_new_attempt(bool protect, bool newBoot)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        string original = (string)(await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"))!;

        long paid = (long)(await fixture.H.ScalarAsync("SELECT SUM(PaidCount) FROM long_rest_provider_effects;"))!;

        LongRestDiscoveryGeneration generation = fixture.Generation;

        LongRestMaintenanceCapability? capability = fixture.Capability;

        if (newBoot)
        {
            generation = generation with { Fence = generation.Fence with { Configuration = generation.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 7 } } };

            LongRestSweepHarness.Require(await fixture.H.Store.StartOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence, generation.Recipe, CancellationToken.None));

            capability = protect ? await fixture.CreateCapabilityAsync(generation) : null;

            if (capability is not null)
            {
                Assert.True((await capability.ValidateWorkAsync(work.Identity, generation.Fence.Configuration, CancellationToken.None)).IsFailure);
            }
        }

        await using var context = fixture.H.Saga.CreateSiblingContext();

        LongRestSweepStore restarted = new(context, TimeProvider.System);

        LongRestSweep current = LongRestSweepHarness.Require(await restarted.BeginOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None));

        LongRestSweepWork resumed = LongRestSweepHarness.Require(await restarted.ReadNextWorkAsync(current, capability, CancellationToken.None))!;

        Assert.NotNull(resumed.AcceptedAdjudication);

        Assert.Equal(newBoot, LongRestDiscoveryHash.Work(work.Identity).Value != LongRestDiscoveryHash.Work(resumed.Identity).Value);

        Assert.Equal(LongRestDiscoveryHash.Work(accepted.Work).Value, LongRestDiscoveryHash.Work(resumed.AcceptedAdjudication.Work).Value);

        Assert.Equal(original, await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"));

        LongRestSweepHarness.Require(await restarted.CommitDecisionAsync(current, resumed.AcceptedAdjudication, capability, CancellationToken.None));

        Assert.Equal(original, await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"));

        Assert.Equal(paid, (long)(await fixture.H.ScalarAsync("SELECT SUM(PaidCount) FROM long_rest_provider_effects;"))!);

        Assert.Equal(1, fixture.AdjudicationSends);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("head")]
    [InlineData("dependency")]
    [InlineData("label")]
    public async Task Head_pin_dependency_or_label_change_after_adjudication_refuses_publication(string change)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        LongRestSourceLeaf leaf = work.Inputs[0];

        if (change == "pin")
        {
            await fixture.H.Saga.Store.SetPinAsync(leaf.Snapshot.MemoryId, true, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        else if (change == "head")
        {
            await fixture.H.Saga.Store.CorrectAsync(leaf.Snapshot.MemoryId, Convert.FromHexString(leaf.Snapshot.ContentHash), "changed", fixture.H.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None);
        }
        else if (change == "dependency")
        {
            await fixture.H.ExecuteAsync("INSERT INTO annal_dependencies (DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence, RelationCode, Ordinal, CreatedAtUtc) SELECT newer.VersionId, newer.Sequence, older.VersionId, older.Sequence, 3, 1, '2026-10-10T00:00:00.0000000Z' FROM annal_versions newer JOIN annal_versions older ON older.Sequence < newer.Sequence WHERE newer.VersionId IN ($dependent, $target) AND older.VersionId IN ($dependent, $target);", ("$dependent", work.Inputs[0].Snapshot.VersionId), ("$target", work.Inputs[1].Snapshot.VersionId));
        }
        else
        {
            // A second normalized label identity is never an interchangeable canonical manifest.
            await fixture.H.ExecuteAsync("INSERT INTO artifact_sensitivity SELECT $label, ArtifactKindCode, replace(ArtifactId, '-', ''), SensitivityCode, ProvenanceModeCode, ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest, ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest, CreatedAtUtc FROM artifact_sensitivity WHERE ArtifactKindCode = 6 AND ArtifactId = $artifact;", ("$label", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$artifact", leaf.CanonicalLabel!.ArtifactId.ToString("D").ToUpperInvariant()));
        }

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));
    }

    [Fact]
    public async Task Provider_NoChange_finishes_the_exact_item_without_inventing_a_canonical_declaration()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work, LongRestAdjudicationKind.NoChange);

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, null, CancellationToken.None));

        Assert.True(committed.Revision > sweep.Revision);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2 AND ReceiptId IS NULL;"));
    }

    private sealed partial class Fixture(LongRestSweepHarness h, LongRestDiscoveryGeneration generation, CovenantTurnLease authority) : IAsyncDisposable
    {
        internal LongRestSweepHarness H { get; } = h;

        internal LongRestDiscoveryGeneration Generation { get; } = generation;

        internal CovenantTurnLease Authority { get; } = authority;

        internal LongRestMaintenanceCapability? Capability { get; private set; }

        internal LongRestSweepStore Store { get; } = new(h.Saga.Context, TimeProvider.System);

        internal int AdjudicationSends { get; private set; }

        internal static async Task<Fixture> CreateAsync(bool protect, int sourceCount = 2, bool campaign = false, bool duplicates = false, bool omittedOwner = false, string? policyFault = null)
        {
            LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

            Guid? campaignId = campaign ? Guid.NewGuid() : null;

            Guid? session = campaign ? await h.SessionAsync(campaignId) : null;

            CovenantDigest? partition = null;

            GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

            for (int index = 1; index <= sourceCount; index++)
            {
                Guid memory = new("10000000-0000-0000-0000-" + index.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));

                await h.SeedAsync(memory.ToString("D"), duplicates ? "identical observation" : "related observation " + index, session);
            }

            await PreparePolicyFaultAsync(h, policyFault);

            if (protect)
            {
                for (int index = 1; index <= sourceCount; index++)
                {
                    partition = await h.ProtectAsync(new Guid("10000000-0000-0000-0000-" + index.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), provenance);
                }
            }

            if (omittedOwner)
            {
                Guid omittedSession = await h.SessionAsync();

                Guid omittedMemory = new("10000000-0000-0000-0000-000000000099");

                await h.SeedAsync(omittedMemory.ToString("D"), "initially omitted owner", omittedSession);

                if (protect)
                {
                    await h.ProtectAsync(omittedMemory, provenance);
                }

                using var cleanup = RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance.Authorize(h.Connection,
                    RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.OwnerCleanup);

                await h.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", omittedSession.ToString("D").ToUpperInvariant()));
            }

            CanonicalCampaignContext canonical = campaign ? CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaignId!.Value), 1, 1, 1, new CovenantDigest(new byte[32])) : CanonicalCampaignContext.GlobalOnly;

            CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(canonical, CancellationToken.None)).Value;

            if (campaign)
            {
                await h.ExecuteAsync("INSERT INTO campaign_path_identities VALUES ($id, 1, 1, '/campaign', 1, zeroblob(32), '2026-10-10T00:00:00.0000000Z');", ("$id", campaignId!.Value.ToString("D").ToUpperInvariant()));

                await h.ExecuteAsync("UPDATE campaign_registry_state SET RegistryEpoch = 1 WHERE StateKey = 1;");
            }

            LongRestDiscoveryGeneration captured = await h.CaptureAsync(campaignId, partition, protect ? authority : null);

            LongRestDiscoveryGeneration published = await h.BuildAsync(captured);

            Fixture fixture = new(h, published, authority);

            fixture.Capability = protect ? await fixture.CreateCapabilityAsync(published) : null;

            return fixture;
        }

        internal async Task<LongRestMaintenanceCapability> CreateCapabilityAsync(LongRestDiscoveryGeneration generation)
        {
            Guid session = await H.SessionAsync(generation.Scope.CampaignId);

            SessionTurnClaimLease claim = LongRestSweepHarness.Require(await new RetroDownfall.Arcanum.Infrastructure.Repositories.SessionTurnClaimStore(
                new RetroDownfall.Arcanum.Tests.Data.Covenant.FixedCovenantConnectionSource(H.Connection), new RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantQuotaGuard(), generation.Fence.Configuration.BootId)
                .AcquireAsync(new(Guid.NewGuid(), 1, Guid.NewGuid(), session, SessionTurnSurface.Intelligence,
                    generation.Fence.Configuration.ValueDigest, generation.Fence.Configuration.ValueDigest, null, 0, 0), CancellationToken.None));

            var invocation = RetroDownfall.Arcanum.Core.Intelligence.ArcanumInvocationContext.Create(
                RetroDownfall.Arcanum.Core.Intelligence.ArcanumExecutionSurface.SessionBackedOperatorTurn, generation.Fence.CanonicalScope,
                RetroDownfall.Arcanum.Core.Intelligence.InvocationAttendance.Attended, CovenantContextPolicy.Default,
                RetroDownfall.Arcanum.Core.Intelligence.ToolPolicy.NoTools,
                CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), Authority.Snapshot.RuntimeAuthorityGeneration, Authority.Snapshot.AuthorityEpoch)).Value;

            LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await LongRestMaintenanceCapability.CreateAsync(generation.OperationId,
                invocation, claim, generation.Fence.CanonicalScope, Authority, generation.Fence.Configuration, CancellationToken.None));

            Assert.True((await capability.ConsumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None)).IsSuccess);

            return capability;
        }

        internal async Task<LongRestSweep> BeginAsync() => LongRestSweepHarness.Require(await Store.BeginOrResumeAsync(Generation.OperationId, Generation.Scope, Generation.Fence.Configuration, CancellationToken.None));

        internal async Task<LongRestAdjudication> AcceptAsync(LongRestSweep sweep, LongRestSweepWork work, LongRestAdjudicationKind kind = LongRestAdjudicationKind.EquivalentObservations, bool persist = true)
        {
            LongRestAttemptStore attempts = new(H.Saga.Context, TimeProvider.System);

            CovenantDigest payload = new(Enumerable.Repeat((byte)83, 32).ToArray());

            LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(work.Identity, LongRestProviderPayloadKind.Adjudication, payload, Generation.Recipe.SummaryDestinationDigest, CancellationToken.None));

            if (Capability is not null)
            {
                attempt = LongRestSweepHarness.Require(await attempts.RecordDisclosureAsync(attempt, payload, CancellationToken.None));
            }

            attempt = LongRestSweepHarness.Require(await attempts.MarkSentAsync(attempt, CancellationToken.None));

            AdjudicationSends++;

            attempt = LongRestSweepHarness.Require(await attempts.CompleteAttemptAsync(attempt, new(LongRestDispatchAttemptState.Accepted, new(15, 3, 18, 0, 0), 0.02m, null), CancellationToken.None));

            LongRestProtectedEvidence? protection = work.Identity.Scope.ProtectedPartitionDigest is { } partition
                ? new(partition, work.Inputs[0].Provenance, [.. work.Inputs.SelectMany(leaf => leaf.LabelDigests)]) : null;

            LongRestAdjudication accepted = new(work.Identity, work.Inputs, new(kind, kind == LongRestAdjudicationKind.Supersession ? work.Inputs[1].Snapshot.VersionId : null), payload, new(attempt, 15, 3, 0.02m), protection);

            return persist ? LongRestSweepHarness.Require(await Store.CommitAcceptedAdjudicationAsync(sweep, accepted, Capability, CancellationToken.None)) : accepted;
        }

        public async ValueTask DisposeAsync()
        {
            await Authority.DisposeAsync();

            await H.DisposeAsync();
        }
    }
}
