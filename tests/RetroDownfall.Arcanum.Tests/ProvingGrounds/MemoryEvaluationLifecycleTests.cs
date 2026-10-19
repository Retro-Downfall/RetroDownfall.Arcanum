using RetroDownfall.Arcanum.Tests.Data.Covenant;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationLifecycleTests
{
    [Theory]
    [InlineData(false, "É:source-1", "é:source1")]
    [InlineData(true, "É:source-1", "é:source1")]
    [InlineData(false, "source-A", "sourcea")]
    public async Task Any_exact_Annals_dependency_erases_whole_evidence_and_preserves_opaque_bystander(bool foreignKeys, string subject, string bystander)
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        await h.Saga.Store.InsertAsync(subject, "selected", DateTimeOffset.UtcNow.AddDays(-1), null, null, "extraction", h.Saga.Embedding(), CancellationToken.None);

        await h.Saga.Store.InsertAsync(bystander, "unrelated", DateTimeOffset.UtcNow.AddDays(-1), null, null, "extraction", h.Saga.Embedding(), CancellationToken.None);

        var source = MemoryEvaluationEvidenceStoreTests.Source() with { SubjectId = subject, Role = MemoryEvaluationCoordinateRole.Dependency };

        var other = MemoryEvaluationEvidenceStoreTests.Source() with { SubjectId = bystander };

        var intervention = MemoryEvaluationEvidenceStoreTests.Source() with { Store = MemoryEvaluationSource.Lexicon, SubjectId = Guid.NewGuid().ToString("N"), VersionId = "lexicon-version", ClaimId = "lexicon-claim" };

        var erased = await SeedAsync(h.Connection, [intervention, source]);

        var retained = await SeedAsync(h.Connection, [other]);

        await ExecuteAsync(h.Connection, foreignKeys ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF");

        Assert.True(await h.Saga.Store.DeleteAsync(subject, CancellationToken.None));

        await AssertAbsentAsync(h.Connection, erased.EvaluationId);

        Assert.True((await Store(h.Connection).GetAsync(retained.EvaluationId, CancellationToken.None)).IsSuccess);

        Assert.True((await Store(h.Connection).CompleteAsync(erased.EvaluationId, MemoryEvaluationEvidenceStoreTests.Report(erased, [intervention, source]), CancellationToken.None)).IsFailure);

        Assert.Equal(1L, await ScalarAsync(h.Connection, "SELECT count(*) FROM saga_memories"));
    }

    [Theory]
    [InlineData(AnnalSubjectStore.Saga)]
    [InlineData(AnnalSubjectStore.Lexicon)]
    public async Task Whole_store_erasure_releases_prepared_and_terminal_roots(AnnalSubjectStore store)
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        var source = MemoryEvaluationEvidenceStoreTests.Source() with { Store = store == AnnalSubjectStore.Saga ? MemoryEvaluationSource.Saga : MemoryEvaluationSource.Lexicon, SubjectId = store == AnnalSubjectStore.Saga ? "subject" : Guid.NewGuid().ToString("N") };

        var first = await SeedAsync(h.Connection, [source]);

        var second = await SeedAsync(h.Connection, [source], terminal: false);

        await ExecuteAsync(h.Connection, "PRAGMA foreign_keys = OFF");

        await AnnalsClaimWriter.DeleteClaimsForStoreAsync(h.Connection, null, store, CancellationToken.None);

        await AssertAbsentAsync(h.Connection, first.EvaluationId);

        await AssertAbsentAsync(h.Connection, second.EvaluationId);
    }

    [Fact]
    public async Task Exact_subject_cleanup_still_removes_evidence_when_its_Annals_claim_is_already_absent()
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        var registration = await SeedAsync(h.Connection, [MemoryEvaluationEvidenceStoreTests.Source() with { SubjectId = "imported:É-1" }]);

        await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(h.Connection, null, AnnalSubjectStore.Saga, "imported:É-1", CancellationToken.None);

        await AssertAbsentAsync(h.Connection, registration.EvaluationId);
    }

    [Fact]
    public async Task Staged_Covenant_entry_plan_counts_complete_evidence_and_proves_captured_ids_absent_with_foreign_keys_off()
    {
        await using var h = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        var head = await h.SeedHeadAsync(CovenantScope.Global, null, "evaluation.key", CovenantLane.Confirmed, CovenantOperation.Set, "fact", CancellationToken.None);

        await InstallEvidenceSchemaAsync(h.Connection);

        var registration = await SeedAsync(h.Connection, [CovenantSource(head.EntryId)]);

        await ExecuteAsync(h.Connection, "PRAGMA foreign_keys = OFF");

        using var authority = CovenantSqliteConnectionInitializer.Instance.Authorize(h.Connection, CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);
        await using var transaction = h.Connection.BeginTransaction();

        var subject = (await CovenantEntryErasurePlan.ReadSubjectAsync(h.Connection, transaction, head.EntryId, CancellationToken.None))!;

        var preview = await CovenantEntryErasurePlan.RunAsync(h.Connection, transaction, subject, CovenantArtifactPlanMode.Count, CovenantEntryErasureMode.Staged, CancellationToken.None);

        foreach (string table in new[] { "memory_evaluation_sources", "memory_evaluation_reports", "memory_evaluations" }) Assert.Equal(1, Assert.Single(preview.Targets, target => target.Table == table).Rows);

        var deleted = await CovenantEntryErasurePlan.RunAsync(h.Connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Staged, CancellationToken.None);

        Assert.Equal(preview.Targets, deleted.Targets);

        Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(h.Connection, transaction, subject, deleted.VersionIds, CancellationToken.None));

        await transaction.CommitAsync();

        await AssertAbsentAsync(h.Connection, registration.EvaluationId);
    }

    [Fact]
    public async Task Real_corrected_saga_owner_closure_uses_canonical_owner_independently_of_null_author()
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid session = await h.Saga.SessionBoundToNewCampaignAsync();

        await h.Saga.Store.InsertAsync("corrected", "old fact", DateTimeOffset.UtcNow.AddMinutes(-5), session, null, "extraction", h.Saga.Embedding(), CancellationToken.None);

        await h.Saga.Store.CorrectAsync("corrected", AnnalContentDigest.ForSagaMemory("old fact"), "correct fact", h.Saga.Embedding(), DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        Guid campaign = Guid.Parse((string)(await ScalarAsync(h.Connection, "SELECT CampaignId FROM saga_memories WHERE Id = 'corrected'"))!);

        await h.AdmitAsync(campaign);

        var snapshot = MemoryEvaluationSourceHarness.Require(await h.Reader.ReadAsync(await h.RequestAsync("corrected"), h.Invocation, h.Lease, CancellationToken.None));

        var source = Assert.Single(snapshot.Coordinates, source => source.Role == MemoryEvaluationCoordinateRole.Intervention);

        Assert.Null(source.SourceSessionId);

        Assert.Equal(session.ToString("D").ToUpperInvariant(), source.CanonicalOwnerSessionId);

        var registration = await SeedAsync(h.Connection, [source], campaign);

        await using var transaction = h.Connection.BeginTransaction();

        Assert.Equal(3, await MemoryEvaluationErasurePlan.ClearSessionAsync(h.Connection, transaction, session, CancellationToken.None));

        await transaction.CommitAsync();

        await AssertAbsentAsync(h.Connection, registration.EvaluationId);

        Assert.Equal(1L, await ScalarAsync(h.Connection, "SELECT count(*) FROM saga_memories WHERE Id = 'corrected'"));
    }

    internal static async Task InstallEvidenceSchemaAsync(SqliteConnection connection)
    {
        foreach (var definition in GrimoireSchemaCatalog.CoreObjects.Where(o => o.Name.StartsWith("memory_evaluation", StringComparison.Ordinal) || o.Name == "grimoire_feature_schemas"))
            await ExecuteAsync(connection, GrimoireSchemaCatalog.Resolve(definition, null));

        await ExecuteAsync(connection, "INSERT OR IGNORE INTO grimoire_feature_schemas(FamilyCode,TransactionTierCode,SchemaVersion,SourceDefinitionFingerprint,InstalledCatalogFingerprint,InstalledAtUtc,HealthCode) VALUES(0,0,19,'" + new string('0',64) + "','sha256:" + new string('0',64) + "','2026-10-18T00:00:00.0000000Z',0)");
    }

    internal static MemoryEvaluationSourceCoordinate CovenantSource(Guid? entry = null) => new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Covenant, (entry ?? Guid.NewGuid()).ToString("D"), null, Guid.NewGuid().ToString("D"), 1, new(new byte[32]), null, null, MemoryEvaluationScopeKind.Global, null, null, null, null, null, MemoryEvaluationSourceLifecycle.Active, ContentSensitivity.CovenantDerived, null, null, Guid.NewGuid(), 1, CovenantLane.Confirmed, CovenantPlacement.GlobalConfirmed, CovenantOrigin.Operator);

    internal static MemoryEvaluationEvidenceStore Store(SqliteConnection connection) => new(new ConnectionSource(connection));

    internal static async Task<MemoryEvaluationRegistration> SeedAsync(SqliteConnection connection, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, Guid? campaign = null, bool terminal = true)
    {
        var registration = MemoryEvaluationEvidenceStoreTests.Registration(sources);

        if (sources.FirstOrDefault(source => source.Store == MemoryEvaluationSource.Covenant)?.Generation is { } generation) registration = registration with { Ownership = registration.Ownership with { DatasetGeneration = generation } };

        if (campaign is { } id) registration = registration with { Ownership = registration.Ownership with { Scope = MemoryEvaluationScopeKind.Campaign, CampaignId = id } };

        var stored = await Store(connection).RegisterAsync(registration, sources, CancellationToken.None);

        Assert.True(stored.IsSuccess, stored.IsFailure ? stored.Error.ToString() : "");

        if (terminal)
        {
            var result = await Store(connection).CompleteAsync(registration.EvaluationId, MemoryEvaluationEvidenceStoreTests.Report(registration, sources), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        }

        return registration;
    }

    internal static async Task AssertAbsentAsync(SqliteConnection connection, Guid id)
    {
        foreach (string table in new[] { "memory_evaluation_reports", "memory_evaluation_sources", "memory_evaluations" })
        {
            await using var command = connection.CreateCommand();

            command.CommandText = $"SELECT count(*) FROM {table} WHERE EvaluationId = $id";

            command.Parameters.AddWithValue("$id", id.ToString("N"));

            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private sealed class ConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);
    }
}
