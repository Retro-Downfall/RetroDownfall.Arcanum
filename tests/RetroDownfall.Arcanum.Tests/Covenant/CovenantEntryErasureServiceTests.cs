using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The Covenant entry erase as a service over a real canonical tier and the real operation gate: the
/// receipt-first replay, the drained closure, every refusal it can meet while draining, and how each
/// uncertain commit leaves the closure.
/// </summary>
/// <remarks>
/// <para>The gate is the production <see cref="CovenantOperationGate"/>. Where a test needs to see the
/// one disposition an erase chose, it wraps that gate in <see cref="DispositionRecordingGate"/>, which
/// hands the real lease back behind a registration that records the disposition on its way through.
/// Nothing about the drain, the closure or the lease is simulated.</para>
///
/// <para>Every refused erase is checked for leaving the entry and its heads exactly as they were, with
/// no receipt and no fingerprint.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantEntryErasureServiceTests
{
    private const string Key = "erasure.service";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly TimeSpan ShortDrain = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan PatientDrain = TimeSpan.FromSeconds(20);

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Replay_is_receipt_first_and_never_closes_a_scope()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        _ = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Result<MemoryErasureResultDto> first = await bed.Service().ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        RecordingCovenantOperationGate recorder = new();

        Result<MemoryErasureResultDto> replayed = await bed.Service(recorder).ApplyAsync(Apply(prepare, "x"), bed.Context, Token);

        Assert.True(replayed.IsSuccess, replayed.IsFailure ? replayed.Error.Message : null);

        Assert.True(replayed.Value.Replayed);

        Assert.Equal(first.Value.EffectDigest, replayed.Value.EffectDigest);

        Assert.Equal([$"read:{CampaignOne:D}"], recorder.Acquisitions);

        Assert.Empty(recorder.RefusedAttempts);

        Assert.Equal(0, recorder.LiveLeases);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// The receipt probe's short read lease is gone before the closure drains, so the drain never waits
    /// on the erase's own lease: at a 150 ms bound such a wait would fail the erase as unmaintainable.
    /// </summary>
    [Fact]
    public async Task The_probe_read_lease_is_released_before_the_scope_closes()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Result<MemoryErasureResultDto> applied = await bed.Service().ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

        Assert.False(applied.Value.Replayed);

        Assert.Equal(1, applied.Value.Local.ErasedItemCount);

        Assert.Equal(preflight.Plan.RowsToRemove, applied.Value.Local.RemovedRowCount);

        Assert.False(await bed.EntryExistsAsync(entryId));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task An_undrained_turn_fails_the_erase_and_changes_nothing()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Snapshot before = await bed.SnapshotAsync(entryId);

        await using CovenantTurnLease turn = (await bed.Gate.AcquireTurnAsync(CampaignContext(), Token)).Value;

        Result<MemoryErasureResultDto> applied = await bed.Service().ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        await bed.AssertIntactAsync(entryId, before);

        await using CovenantReadLease reopened = (await bed.Gate.AcquireReadAsync(EntryScope(), Token)).Value;

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// An authority change made while the closure drains is seen only by revalidating the lease the
    /// drain produced, because the lease records the authority from before the drain. The erase does
    /// that straight after acquiring, and refuses as a stale snapshot with nothing changed.
    /// </summary>
    /// <remarks>
    /// The fixture's authority change retires the runtime authority generation outright, after which
    /// the gate admits no lease of any kind, so the reopen is read off the recorded disposition rather
    /// than off a later acquisition.
    /// </remarks>
    [Fact]
    public async Task Authority_changed_during_the_drain_refuses_with_rollback_and_reopen()
    {
        await using EraseBed bed = await EraseBed.StartAsync(PatientDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Snapshot before = await bed.SnapshotAsync(entryId);

        List<string> recorded = [];

        DispositionRecordingGate gate = new(bed.Gate, recorded);

        CovenantTurnLease turn = (await bed.Gate.AcquireTurnAsync(CampaignContext(), Token)).Value;

        Task<Result<MemoryErasureResultDto>> applying = bed.Service(gate).ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        await WaitForAsync(() => turn.Revocation.IsCancellationRequested);

        bed.Authority.Advance();

        await turn.DisposeAsync();

        Result<MemoryErasureResultDto> applied = await applying;

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, applied.Error.Code);

        Assert.Equal(["complete:RollbackAndReopen"], recorded);

        await bed.AssertIntactAsync(entryId, before);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task A_campaign_deleted_during_the_drain_is_refused_inside_the_transaction()
    {
        await using EraseBed bed = await EraseBed.StartAsync(PatientDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Snapshot before = await bed.SnapshotAsync(entryId);

        CovenantTurnLease turn = (await bed.Gate.AcquireTurnAsync(CampaignContext(), Token)).Value;

        Task<Result<MemoryErasureResultDto>> applying = bed.Service().ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        await WaitForAsync(() => turn.Revocation.IsCancellationRequested);

        await using (SqliteConnection other = await bed.Fixture.OpenAdditionalConnectionAsync(Token))
        {
            await using SqliteCommand delete = other.CreateCommand();

            delete.CommandText = """DELETE FROM "Campaigns" WHERE lower("Id") = $campaign;""";

            _ = delete.Parameters.AddWithValue("$campaign", CampaignOne.ToString("D"));

            Assert.Equal(1, await delete.ExecuteNonQueryAsync(Token));
        }

        await turn.DisposeAsync();

        Result<MemoryErasureResultDto> applied = await applying;

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, applied.Error.Code);

        await bed.AssertIntactAsync(entryId, before);

        await using CovenantReadLease reopened = (await bed.Gate.AcquireReadAsync(EntryScope(), Token)).Value;

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task Agent_publication_during_the_drain_makes_the_erase_a_revision_conflict()
    {
        await using EraseBed bed = await EraseBed.StartAsync(PatientDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Assert.Null(prepare.Proposed);

        CovenantTurnLease turn = (await bed.Gate.AcquireTurnAsync(CampaignContext(), Token)).Value;

        Task<Result<MemoryErasureResultDto>> applying = bed.Service().ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        await WaitForAsync(() => turn.Revocation.IsCancellationRequested);

        // The drained turn publishes its staged proposal before it lets go, as a turn's finalization does.
        long keyEpoch = await bed.ScalarAsync($"SELECT COALESCE(MAX(KeyEpoch), 0) FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';");

        Result<IReadOnlyList<CovenantMutationReceipt>> published = await CovenantMutationFixture.ApplyAsync(
            bed.Fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                bed.Fixture,
                Token,
                CovenantMutationFixture.AgentPropose(CampaignOne, Key, "Agent text.", 0, keyEpoch)),
            Token);

        Assert.True(published.IsSuccess, published.IsFailure ? published.Error.Message : null);

        Snapshot afterPublication = await bed.SnapshotAsync(entryId);

        await turn.DisposeAsync();

        Result<MemoryErasureResultDto> applied = await applying;

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, applied.Error.Code);

        await bed.AssertIntactAsync(entryId, afterPublication);

        Assert.Equal(1, await bed.ScalarAsync($"SELECT count(*) FROM covenant_heads WHERE NormalizedKey = '{Key}' AND LaneCode = 2;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// Reclaiming a key purges its curation in every scope, so only an installation closure may do it.
    /// The gate stub hands back a Campaign closure for a reclaiming erase.
    /// </summary>
    [Fact]
    public async Task Reclamation_is_refused_when_the_held_lease_does_not_cover_the_installation()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Assert.True(preflight.Plan.Covenant!.ReclaimsKey);

        Snapshot before = await bed.SnapshotAsync(entryId);

        List<string> recorded = [];

        DispositionRecordingGate gate = new(bed.Gate, recorded) { ForceCampaignClosure = true };

        Result<MemoryErasureResultDto> applied = await bed.Service(gate).ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, applied.Error.Code);

        Assert.Equal(["complete:RollbackAndReopen"], recorded);

        await bed.AssertIntactAsync(entryId, before);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task An_uncertain_commit_with_a_receipt_reopens_committed()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        List<string> recorded = [];

        CovenantEntryErasureService service = bed.Service(
            new DispositionRecordingGate(bed.Gate, recorded),
            commit: static async (transaction, cancellationToken) =>
            {
                await transaction.CommitAsync(cancellationToken);

                throw new SqliteException("disk I/O error", 10);
            });

        Result<MemoryErasureResultDto> applied = await service.ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

        Assert.False(applied.Value.Replayed);

        Assert.Equal(["complete:CommitAndReopen"], recorded);

        Assert.Equal(1, await bed.ReceiptsAsync(prepare.MutationId));

        Assert.False(await bed.EntryExistsAsync(entryId));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// A commit that failed without persisting reports a retryable failure and no result: the erase
    /// rolled back, nothing reached the scrub, and the closure reopens as a rollback.
    /// </summary>
    [Fact]
    public async Task An_uncertain_commit_without_a_receipt_reopens_rolled_back()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Snapshot before = await bed.SnapshotAsync(entryId);

        List<string> recorded = [];

        CovenantEntryErasureService service = bed.Service(
            new DispositionRecordingGate(bed.Gate, recorded),
            commit: static (_, _) => throw new SqliteException("disk I/O error", 10),
            recorder: recorded);

        Result<MemoryErasureResultDto> applied = await service.ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        // The receipt was read back on a fresh read-only connection, and the scrub, which needs a
        // read-write one, never ran.
        Assert.Equal(["fresh:ReadOnly", "complete:RollbackAndReopen"], recorded);

        await bed.AssertIntactAsync(entryId, before);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task An_uncertain_commit_whose_re_read_fails_stays_closed_and_reports_manual_recovery()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        Snapshot before = await bed.SnapshotAsync(entryId);

        List<string> recorded = [];

        CovenantEntryErasureService service = bed.Service(
            new DispositionRecordingGate(bed.Gate, recorded),
            commit: static (_, _) => throw new SqliteException("disk I/O error", 10),
            reread: static (_, _) => Task.FromResult(Result<MemoryErasureReceiptRow?>.Failure(
                new Error(ErrorCodes.MemoryErasure.Unavailable, "The receipt could not be read back."))));

        Result<MemoryErasureResultDto> applied = await service.ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, applied.Error.Code);

        Assert.Equal(["complete:KeepClosed"], recorded);

        await bed.AssertIntactAsync(entryId, before);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task A_cancelled_request_after_commit_still_reopens_the_scope()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        Guid entryId = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        using CancellationTokenSource request = new();

        CovenantEntryErasureService service = bed.Service(
            commit: async (transaction, cancellationToken) =>
            {
                await transaction.CommitAsync(cancellationToken);

                await request.CancelAsync();
            });

        Result<MemoryErasureResultDto> applied = await service.ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, request.Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

        Assert.False(applied.Value.Replayed);

        Assert.Equal(1, await bed.ReceiptsAsync(prepare.MutationId));

        Assert.False(await bed.EntryExistsAsync(entryId));

        await using CovenantReadLease reopened = (await bed.Gate.AcquireReadAsync(EntryScope(), CancellationToken.None)).Value;

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    [Fact]
    public async Task The_wal_checkpoint_runs_only_after_the_closure_reopens()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        _ = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) = await bed.PrepareAsync(bed.Service());

        List<string> recorded = [];

        CovenantEntryErasureService service = bed.Service(new DispositionRecordingGate(bed.Gate, recorded), recorder: recorded);

        Result<MemoryErasureResultDto> applied = await service.ApplyAsync(Apply(prepare, preflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

        Assert.Equal(["complete:CommitAndReopen", "fresh:ReadWrite"], recorded);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// The key is opened, and on a fresh installation created, before the installation read lease is
    /// taken, so no keychain I/O ever happens while a Covenant lease is held.
    /// </summary>
    [Fact]
    public async Task The_preparer_holds_an_installation_read_lease_taken_after_the_key()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        _ = await bed.SetCampaignEntryAsync("Campaign text.");

        DispositionRecordingGate gate = new(bed.Gate, []) { CredentialCalls = () => bed.Fixture.Credentials.Calls };

        CovenantErasePrepareRequest request = await bed.RequestAsync(Guid.NewGuid());

        Result<CovenantEntryErasurePrepared> prepared = await bed.Service(gate).PrepareHeldAsync(request, bed.Context, Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? $"{prepared.Error.Code}: {prepared.Error.Message}" : null);

        await using ICovenantSnapshotReadLease lease = prepared.Value.ReadLease;

        Assert.Equal(CovenantLeaseKind.InstallationRead, lease.Snapshot.Kind);

        Assert.True(gate.CredentialCallsAtInstallationRead > 0, "The key was not read before the read lease was taken.");

        Assert.Equal(gate.CredentialCallsAtInstallationRead, bed.Fixture.Credentials.Calls);

        Assert.True((await lease.RevalidateAsync(Token)).IsSuccess);
    }

    /// <summary>
    /// An entry already erased by another mutation takes no closure: the transaction alone answers it,
    /// and with a receipt naming it the answer is 410.
    /// </summary>
    [Fact]
    public async Task A_second_mutation_on_an_erased_entry_takes_no_closure_and_answers_410()
    {
        await using EraseBed bed = await EraseBed.StartAsync(ShortDrain);

        _ = await bed.SetCampaignEntryAsync("Campaign text.");

        (CovenantErasePrepareRequest first, MemoryErasurePreflightDto firstPreflight) = await bed.PrepareAsync(bed.Service());

        (CovenantErasePrepareRequest second, MemoryErasurePreflightDto secondPreflight) = await bed.PrepareAsync(bed.Service());

        Assert.True((await bed.Service().ApplyAsync(Apply(first, firstPreflight.PreflightToken), bed.Context, Token)).IsSuccess);

        List<string> recorded = [];

        DispositionRecordingGate gate = new(bed.Gate, recorded);

        Result<MemoryErasureResultDto> applied = await bed.Service(gate).ApplyAsync(Apply(second, secondPreflight.PreflightToken), bed.Context, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.SubjectErased, applied.Error.Code);

        Assert.Empty(gate.Attempts);

        Assert.Empty(recorded);

        Assert.Equal(0, await bed.ReceiptsAsync(second.MutationId));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(bed.Connection, Token);
    }

    /// <summary>
    /// A catalog whose canonical tier predates version 6 cannot record an entry erasure, and prepare
    /// says so before it touches the keychain, so no key is created for an erase that cannot happen.
    /// </summary>
    [Fact]
    public async Task An_erase_below_canonical_six_is_unavailable()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CovenantCanonicalSchemaVersionFiveFixture.ChainSet(),
            1536,
            Token);

        Assert.Equal(5, installed.CovenantCanonical.SchemaVersion);

        InMemoryOsCredentialStore stored = new();

        CountingOsCredentialStore credentials = new(stored);

        using MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated(credentials);

        CovenantEntryErasureService service = new(
            new FixedCovenantConnectionSource(connection),
            keyring,
            keyring,
            new TestEnvelopeCodec(),
            new MemoryErasureScrubber(new RefusingFreshConnections()),
            new RecordingCovenantOperationGate(),
            CovenantSqliteConnectionInitializer.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            TimeProvider.System);

        Result<MemoryErasurePreflightDto> prepared = await service.PrepareAsync(
            new CovenantErasePrepareRequest(
                CovenantScope.Global,
                null,
                Key,
                Guid.NewGuid(),
                new CovenantEraseHeadExpectation(Guid.NewGuid(), 1),
                null,
                Guid.NewGuid()),
            OperatorAuthorityContext.CreateForTests(CovenantAuthorityRequirement.LifecycleManage, Guid.NewGuid(), 1, 1, 1),
            Token);

        Assert.True(prepared.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, prepared.Error.Code);

        Assert.Equal(0, credentials.Calls);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            stored.TryGet(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount).Status);
    }

    private static CovenantEraseRequest Apply(CovenantErasePrepareRequest prepare, string token) =>
        new(prepare.Scope, prepare.CampaignId, prepare.Key, prepare.EntryId, prepare.Confirmed, prepare.Proposed, prepare.MutationId, token);

    private static CovenantOperationScope EntryScope() => CovenantOperationScope.ForCampaign(CampaignOne);

    private static CanonicalCampaignContext CampaignContext() => CovenantOperationGateFixture.CampaignContext(CampaignOne);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Token);
        }

        Assert.Fail("The awaited drain never started.");
    }

    /// <summary>What a refused erase must leave exactly as it found it.</summary>
    private sealed record Snapshot(long Entries, long Heads, long Versions, long CurationRows);

    /// <summary>
    /// A canonical tier at version 6 with the Core evidence tables, one Campaign, and the real gate
    /// bound to that tier's dataset generation.
    /// </summary>
    private sealed class EraseBed : IAsyncDisposable
    {
        private readonly CovenantServiceHarness _harness;

        private EraseBed(
            CovenantServiceHarness harness,
            CovenantOperationGate gate,
            FakeCovenantAuthorityProvider authority,
            OperatorAuthorityContext context)
        {
            _harness = harness;

            Gate = gate;

            Authority = authority;

            Context = context;
        }

        internal CovenantCanonicalFixture Fixture => _harness.Fixture;

        internal SqliteConnection Connection => Fixture.Connection;

        internal CovenantOperationGate Gate { get; }

        internal FakeCovenantAuthorityProvider Authority { get; }

        internal OperatorAuthorityContext Context { get; }

        internal TestEnvelopeCodec Codec { get; } = new();

        internal static async Task<EraseBed> StartAsync(TimeSpan drainTimeout)
        {
            CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(
                Token,
                withErasureEvidence: true,
                coreObjects: ["memory_erasure_receipts", "memory_erasure_receipt_subjects", "memory_erasure_receipts_guard_update"]);

            try
            {
                // The installer records every tier it installs; the scratch tier records only Core, so
                // the canonical row the erase reads its readiness from is written the way it would be.
                await using (SqliteCommand command = harness.Fixture.Connection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO grimoire_feature_schemas (
                            FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                            InstalledCatalogFingerprint, InstalledAtUtc, HealthCode, HealthDetailCode)
                        VALUES (1, 1, $version, $source, $installed, '2026-08-20T00:00:00.0000000Z', 0, NULL);
                        """;

                    _ = command.Parameters.AddWithValue("$version", GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion);

                    _ = command.Parameters.AddWithValue("$source", new string('0', 64));

                    _ = command.Parameters.AddWithValue("$installed", "sha256:" + new string('0', 64));

                    _ = await command.ExecuteNonQueryAsync(Token);
                }

                await harness.AddCampaignAsync(CampaignOne, Token);

                Guid generation = await harness.Fixture.ReadDatasetGenerationAsync(Token);

                FakeCovenantAvailability availability = new();

                availability.Mutate(current => current with { DatasetGeneration = generation });

                FakeCovenantAuthorityProvider authority = new();

                CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
                    availability,
                    authority,
                    new FakeCovenantCampaignScopeProbe(),
                    drainTimeout);

                OperatorAuthorityContext context = new OperatorAuthorityContextIssuer(authority)
                    .Issue(CovenantAuthorityRequirement.LifecycleManage)
                    .Value;

                return new EraseBed(harness, gate, authority, context);
            }
            catch
            {
                await harness.DisposeAsync();

                throw;
            }
        }

        internal CovenantEntryErasureService Service(
            ICovenantOperationGate? gate = null,
            Func<SqliteTransaction, CancellationToken, Task>? commit = null,
            Func<Guid, CancellationToken, Task<Result<MemoryErasureReceiptRow?>>>? reread = null,
            List<string>? recorder = null) =>
            new(
                new FixedCovenantConnectionSource(Connection),
                Fixture.ErasureKeys,
                Fixture.ErasureKeys,
                Codec,
                new MemoryErasureScrubber(new ScratchFreshConnections(Fixture, recorder)),
                gate ?? Gate,
                CovenantSqliteConnectionInitializer.Instance,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                TimeProvider.System)
            {
                CommitForTesting = commit,
                ReceiptReReadForTesting = reread,
            };

        /// <summary>Writes the erased key's Campaign entry through the production prepare-and-commit path.</summary>
        internal async Task<Guid> SetCampaignEntryAsync(string content)
        {
            await _harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, content, Token);

            return (await RequestAsync(Guid.NewGuid())).EntryId;
        }

        /// <summary>The erase target exactly as show reports it: the entry and both lane heads.</summary>
        internal async Task<CovenantErasePrepareRequest> RequestAsync(Guid mutationId)
        {
            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                SELECT EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision
                FROM covenant_heads
                WHERE CampaignId = $campaign AND NormalizedKey = $key;
                """;

            _ = command.Parameters.AddWithValue("$campaign", CampaignOne.ToString("D"));

            _ = command.Parameters.AddWithValue("$key", Key);

            Guid? entryId = null;

            CovenantEraseHeadExpectation? confirmed = null;

            CovenantEraseHeadExpectation? proposed = null;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

            while (await reader.ReadAsync(Token))
            {
                entryId = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture);

                CovenantEraseHeadExpectation head = new(Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt64(3));

                if (reader.GetInt32(1) == (int)CovenantLane.Confirmed)
                {
                    confirmed = head;
                }
                else
                {
                    proposed = head;
                }
            }

            Assert.NotNull(entryId);

            return new CovenantErasePrepareRequest(
                CovenantScope.Campaign,
                CampaignOne,
                Key,
                entryId!.Value,
                confirmed,
                proposed,
                mutationId);
        }

        internal async Task<(CovenantErasePrepareRequest Prepare, MemoryErasurePreflightDto Preflight)> PrepareAsync(
            CovenantEntryErasureService service)
        {
            CovenantErasePrepareRequest request = await RequestAsync(Guid.NewGuid());

            Result<MemoryErasurePreflightDto> prepared = await service.PrepareAsync(request, Context, Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? $"{prepared.Error.Code}: {prepared.Error.Message}" : null);

            return (request, prepared.Value);
        }

        internal async Task<Snapshot> SnapshotAsync(Guid entryId) =>
            new(
                await ScalarAsync($"SELECT count(*) FROM covenant_entries WHERE EntryId = '{entryId:D}';"),
                await ScalarAsync($"SELECT count(*) FROM covenant_heads WHERE EntryId = '{entryId:D}';"),
                await ScalarAsync($"SELECT count(*) FROM covenant_versions WHERE EntryId = '{entryId:D}';"),
                await ScalarAsync($"SELECT (SELECT count(*) FROM covenant_curation_heads) + (SELECT count(*) FROM covenant_curation_versions) + (SELECT count(*) FROM covenant_curation_receipts);"));

        /// <summary>The entry and its heads exactly as before, and no receipt or fingerprint anywhere.</summary>
        internal async Task AssertIntactAsync(Guid entryId, Snapshot before)
        {
            Assert.Equal(1, before.Entries);

            Assert.Equal(before, await SnapshotAsync(entryId));

            Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM memory_erasure_receipts;"));

            Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM memory_erasure_fingerprints;"));
        }

        internal async Task<bool> EntryExistsAsync(Guid entryId) =>
            await ScalarAsync($"SELECT count(*) FROM covenant_entries WHERE EntryId = '{entryId:D}';") == 1;

        internal Task<long> ReceiptsAsync(Guid mutationId) =>
            ScalarAsync($"SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = '{mutationId.ToString("D").ToUpperInvariant()}';");

        internal async Task<long> ScalarAsync(string sql)
        {
            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
        }

        public ValueTask DisposeAsync() => _harness.DisposeAsync();
    }

    /// <summary>
    /// The real gate, with every entry-erasure lease it grants handed back behind a registration that
    /// records the one disposition the erase chose before passing it on.
    /// </summary>
    private sealed class DispositionRecordingGate(ICovenantOperationGate inner, List<string> recorder) : ICovenantOperationGate
    {
        /// <summary>The gate stub's lie: a reclaiming erase is handed a Campaign closure anyway.</summary>
        internal bool ForceCampaignClosure { get; init; }

        /// <summary>Reads how many credential calls had been made when the installation read was asked for.</summary>
        internal Func<int>? CredentialCalls { get; init; }

        internal int? CredentialCallsAtInstallationRead { get; private set; }

        internal List<string> Attempts { get; } = [];

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(CancellationToken cancellationToken)
        {
            CredentialCallsAtInstallationRead = CredentialCalls?.Invoke();

            return inner.AcquireInstallationReadAsync(cancellationToken);
        }

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            inner.AcquireReadAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            inner.AcquireWriteAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(CanonicalCampaignContext campaign, CancellationToken cancellationToken) =>
            inner.AcquireTurnAsync(campaign, cancellationToken);

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            inner.AcquireMcpAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(CancellationToken cancellationToken) =>
            inner.AcquireAcceleratorAsync(cancellationToken);

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            inner.AcquireCleanupAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireProtectedTransferAsync(scope, owner, cancellationToken);

        public async ValueTask<Result<CovenantEntryErasureLease>> AcquireEntryErasureAsync(
            CovenantOperationScope entryScope,
            bool reclaimsKey,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {
            lock (Attempts)
            {
                Attempts.Add($"entry-erasure:{reclaimsKey}");
            }

            Result<CovenantEntryErasureLease> acquired = await inner
                .AcquireEntryErasureAsync(entryScope, !ForceCampaignClosure && reclaimsKey, owner, cancellationToken);

            return acquired.IsFailure
                ? acquired
                : Result<CovenantEntryErasureLease>.Success(new CovenantEntryErasureLease(new RecordingRegistration(acquired.Value, recorder)));
        }

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.AcquireExclusiveAsync(owner, cancellationToken);

        public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeOrAcquireExclusiveAsync(owner, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeProtectedTransferAsync(scope, owner, cancellationToken);

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            inner.ResumeExclusiveAsync(owner, cancellationToken);

        /// <summary>The real lease, with its disposition written down on the way through.</summary>
        private sealed class RecordingRegistration(CovenantEntryErasureLease lease, List<string> recorder)
            : ICovenantExclusiveLeaseRegistration
        {
            public CovenantOperationLeaseSnapshot Snapshot => lease.Snapshot;

            public CancellationToken Revocation => lease.Revocation;

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => lease.RevalidateAsync(cancellationToken);

            public ValueTask ReleaseAsync() => lease.DisposeAsync();

            public Result ExecuteWhileHeld(Func<Result> callback) => lease.ExecuteWhileHeld(callback);

            public ValueTask<Result> CompleteAsync(CovenantExclusiveLeaseDisposition disposition, CancellationToken cancellationToken)
            {
                lock (recorder)
                {
                    recorder.Add($"complete:{disposition}");
                }

                return lease.CompleteAsync(disposition, cancellationToken);
            }
        }
    }

    /// <summary>Opens a fresh connection to the scratch tier for each request, writing down which kind.</summary>
    private sealed class ScratchFreshConnections(CovenantCanonicalFixture fixture, List<string>? recorder) : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The erase opens only fresh connections of its own.");

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            if (recorder is not null)
            {
                lock (recorder)
                {
                    recorder.Add($"fresh:{kind}");
                }
            }

            SqliteConnection connection = await fixture.OpenAdditionalConnectionAsync(cancellationToken);

            return Result<IGrimoireOrdinaryConnectionLease>.Success(new FreshLease(connection));
        }

        private sealed class FreshLease(SqliteConnection connection) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => connection;

            public void Dispose() => connection.Dispose();

            public ValueTask DisposeAsync() => connection.DisposeAsync();
        }
    }

    /// <summary>A factory that refuses every open, for a service that must never reach one.</summary>
    private sealed class RefusingFreshConnections : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// An envelope codec that authenticates by construction rather than by key material, with the
/// production expiry rule on the system clock.
/// </summary>
internal sealed class TestEnvelopeCodec : ICovenantEnvelopeCodec
{
    private readonly Dictionary<string, CovenantEnvelopeBody> _issued = new(StringComparer.Ordinal);

    private readonly Lock _sync = new();

    public CovenantEnvelopeKeySnapshot KeySnapshot { get; } = new(1, 1, 1, Guid.NewGuid().ToString("D"), Guid.NewGuid());

    public Result<string> Encode(
        CovenantEnvelopePurpose purpose,
        ReadOnlySpan<byte> payload,
        TimeSpan lifetime,
        DateTimeOffset? issuedAtUtc = null)
    {
        string token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

        DateTimeOffset now = issuedAtUtc ?? DateTimeOffset.UtcNow;

        lock (_sync)
        {
            _issued[token] = new CovenantEnvelopeBody(
                purpose,
                1,
                1,
                (ulong)_issued.Count + 1,
                DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds((now + lifetime).ToUnixTimeMilliseconds()),
                payload.ToArray());
        }

        return Result<string>.Success(token);
    }

    public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token)
    {
        CovenantEnvelopeBody? body;

        lock (_sync)
        {
            if (token is null || !_issued.TryGetValue(token, out body))
            {
                body = null;
            }
        }

        if (body is null || body.Purpose != expectedPurpose)
        {
            return Result<CovenantEnvelopeBody>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant token is not valid for this purpose."));
        }

        return DateTimeOffset.UtcNow >= body.ExpiresAtUtc
            ? Result<CovenantEnvelopeBody>.Failure(CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.Expired))
            : Result<CovenantEnvelopeBody>.Success(body);
    }
}
