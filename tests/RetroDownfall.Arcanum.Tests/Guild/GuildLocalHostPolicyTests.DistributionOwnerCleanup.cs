using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Distribution_owner_cleanup_erases_protected_offer_bid_and_note_but_retains_replay_and_accounting_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await OperatorAwardFixtureAsync(f);

        Guid cohortId = setup.Offer.CohortId;

        var current = await setup.Store.GetAsync(setup.IssuerCaller, cohortId, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        AppendSharedNoteRequest request = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation,
            setup.Offer.Id, "owner-cleanup shared-note canary", null);

        var note = await setup.Store.AppendNoteAsync(setup.IssuerCaller, NoteMutation(request), cohortId, request, default);

        Assert.True(note.IsSuccess, note.Error.Message);

        GuildDataRef noteRef = Assert.IsType<GuildDataRef>(note.Value.BodyRef);

        // Settle real physical-effect and result obligations before owner deletion. Cleanup
        // must preserve this evidence rather than treating deletion as execution stop proof.
        await CommitConvergenceResultAsync(f, setup.Owner, setup.Primary, "owner-cleanup member result", retainedRun: setup.PrimaryCaller);

        var primary = await f.Store.GetAsync(setup.Owner, setup.Primary.Quest.Id, default);

        var issuer = await f.Store.GetAsync(setup.Owner, setup.Issuer.Quest.Id, default);

        Assert.True(primary.IsSuccess, primary.Error.Message);

        Assert.True(issuer.IsSuccess, issuer.Error.Message);

        string primaryDigest = primary.Value.Quest.ResultRef!.Digest;

        var applied = await f.Store.ApplyResultAsync(setup.Owner,
            new(new(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData("owner-cleanup apply"u8))),
                issuer.Value.Quest.Id, primary.Value.Quest.Id, primary.Value.Quest.ResultReceiptId!.Value,
                primaryDigest, issuer.Value.Quest.Revision), default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        current = await setup.Store.GetAsync(setup.IssuerCaller, cohortId, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await setup.Store.CloseAsync(setup.IssuerCaller, cohortId, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto? convergence = null;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            var stopped = await f.Store.RefreshCancellationConvergenceAsync(setup.Owner, setup.Primary.Quest.Id, default);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            var advanced = await setup.Store.ReconcileAsync(setup.Owner, cohortId, default);

            Assert.True(advanced.IsSuccess || advanced.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, advanced.Error.Message);

            if (advanced.IsSuccess) convergence = advanced.Value;

            if (convergence?.State == GuildCohortState.Completed) break;
        }

        Assert.Equal(GuildCohortState.Completed, convergence?.State);

        await CommitConvergenceResultAsync(f, setup.Owner, setup.Issuer, "owner-cleanup root result", retainedRun: setup.IssuerCaller);

        issuer = await f.Store.GetAsync(setup.Owner, setup.Issuer.Quest.Id, default);

        Assert.True(issuer.IsSuccess, issuer.Error.Message);

        var acknowledged = await f.Store.AcknowledgeRootResultAsync(setup.Owner, issuer.Value.Quest.Id,
            issuer.Value.Quest.ResultReceiptId!.Value, issuer.Value.Quest.ResultRef!.Digest, 1, default);

        Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        GuildDataRef[] references = [setup.Offer.GoalRef, setup.Bid.NoteRef, noteRef];

        // Current sensitivity may escalate after publication. Use the canonical label writer
        // with the exact retained source owner/revision/digest, without rewriting immutable rows.
        await using (SqliteConnection marking = await f.OpenAsync())
        await using (SqliteTransaction transaction = marking.BeginTransaction(deferred: false))
        {
            foreach (GuildDataRef reference in references)
            {
                Guid session = reference.ContentId == setup.Bid.NoteRef.ContentId
                    ? setup.Primary.Quest.SessionId!.Value : setup.Issuer.Quest.SessionId!.Value;

                var marked = await ArtifactSensitivityLedger.WriteWithinAsync(marking, transaction,
                    new(SensitiveArtifactKind.GuildContent, reference.ContentId, session, null, null,
                        (ulong)reference.Revision, new CovenantDigest(Convert.FromHexString(reference.Digest)),
                        ContentSensitivity.CovenantDerived,
                        GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration])), default);

                Assert.True(marked.IsSuccess, marked.Error.Message);
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17 AND SensitivityCode=1"));

        long mutationReceipts = await f.ScalarAsync("SELECT count(*) FROM guild_work_mutations");

        long accountingEvidence = await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence");

        long settledAccounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=1 AND SettlementReceiptId IS NOT NULL");

        Assert.Equal(2L, mutationReceipts);

        Assert.True(accountingEvidence >= 2);

        Assert.Equal(3L, settledAccounts);

        Guid issuerSession = setup.Issuer.Quest.SessionId!.Value;

        Guid primarySession = setup.Primary.Quest.SessionId!.Value;

        await using SqliteConnection connection = await f.OpenAsync();

        using (CovenantSqliteAuthorizationScope deletion = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await using SqliteCommand remove = connection.CreateCommand();

            remove.CommandText = "DELETE FROM Sessions WHERE Id IN($issuer,$primary)";

            remove.Parameters.AddWithValue("$issuer", issuerSession.ToString("D").ToUpperInvariant());

            remove.Parameters.AddWithValue("$primary", primarySession.ToString("D").ToUpperInvariant());

            Assert.Equal(2, await remove.ExecuteNonQueryAsync());
        }

        FixedCovenantConnectionSource source = new(connection);

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantSensitivePurgeAuthorityScope scope = new();

        Assert.True(scope.Publish(CovenantErasureAuthorityFixture.OperatorContext(authority)).IsSuccess);

        ArtifactSensitivityLedger labels = new(source);

        CovenantSensitiveRetentionPurgeCoordinator purger = new(labels, source,
            new CovenantManagedFileErasureRequestReader(), CovenantOperationGateFixture.CreateGate(availability, authority),
            CovenantErasureAuthorityFixture.Issuer(authority), availability,
            new CovenantProtectedArtifactErasureKernel(source, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System),
            new DistributionNoManagedFiles(), scope, NullLogger<CovenantSensitiveRetentionPurgeCoordinator>.Instance);

        DistributionPurgeObservation observedPurge = new(purger);

        GuildOwnerCleanupCoordinator cleanup = new(f.Connections, f.Gate, observedPurge,
            new CovenantLabeledArtifactGuard(labels, NullLogger<CovenantLabeledArtifactGuard>.Instance),
            [new GuildRosterCleanupParticipant(), new GuildSignetCleanupParticipant(f.Gate),
                new GuildPortalCleanupParticipant(TimeProvider.System), new GuildQuestCleanupParticipant(f.Connections, TimeProvider.System),
                new GuildCohortCleanupParticipant(f.Connections, setup.Store, f.Policy), new GuildWorkspaceLeaseCleanupParticipant(f.Connections)],
            TimeProvider.System);

        string last = "not attempted";

        for (int attempt = 0; attempt < 40; attempt++)
        {
            var progressed = await cleanup.RunBatchAsync(8, 8, default);

            last = progressed.IsSuccess ? "successful batch" : $"{progressed.Error.Code}: {progressed.Error.Message}; purge={observedPurge.Last}";

            Assert.True(progressed.IsSuccess || progressed.Error.Code == ErrorCodes.Guild.ReconciliationRequired, last);

            if (await f.ScalarAsync("SELECT count(*) FROM guild_owner_cleanup WHERE OwnerKindCode=2 AND PhaseCode=6") == 2) break;
        }

        Assert.True(await f.ScalarAsync("SELECT count(*) FROM guild_owner_cleanup WHERE OwnerKindCode=2 AND PhaseCode=6") == 2, last);

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_content WHERE SessionId COLLATE NOCASE IN('{issuerSession:D}','{primarySession:D}')"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohorts WHERE Id='{cohortId:D}' COLLATE NOCASE AND Retired=1 AND Name IS NULL"));

        foreach (GuildDataRef reference in references)
        {
            Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_content_erasure_receipts WHERE ContentId='{reference.ContentId:D}' COLLATE NOCASE AND Digest=X'{reference.Digest}' AND Revision={reference.Revision} AND ByteLength={reference.ByteLength}"));
        }

        Assert.Equal(mutationReceipts, await f.ScalarAsync("SELECT count(*) FROM guild_work_mutations"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_ledger WHERE Id='{note.Value.Id:D}' COLLATE NOCASE AND IdempotencyKey='{request.IdempotencyKey:D}' COLLATE NOCASE AND ReceiptId='{note.Value.Receipt!.Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_offers WHERE Id='{setup.Offer.Id:D}' COLLATE NOCASE AND GoalContentId='{setup.Offer.GoalRef.ContentId:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_bids WHERE Id='{setup.Bid.Id:D}' COLLATE NOCASE AND NoteContentId='{setup.Bid.NoteRef.ContentId:D}' COLLATE NOCASE"));

        Assert.Equal(accountingEvidence, await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));

        Assert.Equal(settledAccounts, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=1 AND SettlementReceiptId IS NOT NULL"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts WHERE WorkStoppedProven=1"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_applications WHERE QuestId='{setup.Primary.Quest.Id:D}' COLLATE NOCASE AND Digest=X'{primaryDigest}'"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1 AND CancellationRequested=1"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_owner_cleanup work JOIN owner_deletion_events source ON source.OwnerKindCode=work.OwnerKindCode AND source.OwnerId=work.OwnerId JOIN capability_cleanup_state family ON family.CapabilityFamilyCode=2 WHERE family.AppliedSessionSequence<source.Sequence"));

        Assert.True((await setup.Store.AppendNoteAsync(setup.IssuerCaller, NoteMutation(request), cohortId, request, default)).IsFailure);

        Assert.True((await setup.Store.GetOfferAsync(setup.Owner, setup.Offer.Id, default)).IsFailure);

        Assert.Equal(mutationReceipts, await f.ScalarAsync("SELECT count(*) FROM guild_work_mutations"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_content WHERE SessionId COLLATE NOCASE IN('{issuerSession:D}','{primarySession:D}')"));
    }

    private sealed class DistributionPurgeObservation(ICovenantSensitiveArtifactPurger inner) : ICovenantSensitiveArtifactPurger
    {
        internal string Last { get; private set; } = "not reached";

        public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(IReadOnlyList<CovenantSensitivePurgeTarget> targets,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.PurgeAsync(targets, cancellationToken);

            Last = result.IsSuccess ? string.Join(",", result.Value.Results.Select(item => $"{item.ArtifactId}:{item.Disposition}:{item.Blocker}"))
                : $"{result.Error.Code}:{result.Error.Message}";

            return result;
        }
    }

    private sealed class DistributionNoManagedFiles : ICovenantManagedFileErasureKernel
    {
        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(CovenantManagedFileErasureRequest request,
            CovenantArtifactErasureAuthority authority, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Distribution database content cannot route to file erasure.");
    }
}
