using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationEvidenceStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly CovenantDigest Digest = new(new byte[32]);

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    private static readonly Guid Dataset = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task Registration_roundtrips_exact_source_cut_edges_and_independent_owner()
    {
        await using var h = await Harness.Create();

        var owner = Guid.NewGuid().ToString("D").ToUpperInvariant();

        ImmutableArray<MemoryEvaluationSourceCoordinate> sources = [Source() with { CanonicalOwnerSessionId = owner }, Source() with { Role = MemoryEvaluationCoordinateRole.Dependency, VersionId = "old", DependencyRelation = AnnalDependencyRelation.Supersedes }];

        ImmutableArray<MemoryEvaluationDependencyEdge> edges = [new(MemoryEvaluationSource.Saga, "version", "old", AnnalDependencyRelation.Supersedes, 0)];

        var registration = Registration(sources, edges);

        var result = Require(await h.Store.RegisterAsync(registration, sources, Ct));

        Assert.Equal(registration.SourceAsOfUtc, result.Registration.SourceAsOfUtc);

        Assert.Equal(edges.ToArray(), result.Registration.Dependencies.ToArray());

        var read = Require(await h.Store.GetAsync(registration.EvaluationId, Ct));

        Assert.Equal(owner, read.Sources[0].CanonicalOwnerSessionId);

        Assert.Null(read.Sources[0].SourceSessionId);

        Assert.Equal(registration.Ownership, Require(await h.Store.ReadOwnershipAsync(registration.EvaluationId, Ct)));

        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));

        Assert.Equal(2L, await h.Scalar("SELECT count(*) FROM memory_evaluation_sources"));

        Require(await h.Store.CompleteAsync(registration.EvaluationId, Report(registration, sources), Ct));

        var terminal = Require(await h.Store.GetAsync(registration.EvaluationId, Ct));

        Assert.Equal(edges.ToArray(), terminal.Report!.Dependencies.ToArray());

        Assert.Equal(registration.SourceAsOfUtc, terminal.Report.Registration.SourceAsOfUtc);
    }

    [SkippableTheory]
    [InlineData(MemoryEvaluationSource.Saga)]
    [InlineData(MemoryEvaluationSource.Lexicon)]
    public async Task Proved_cross_store_Annals_dependencies_roundtrip_registration_and_terminal(MemoryEvaluationSource dependentStore)
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        LexiconService lexicon = new(h.Saga.Context, NullLogger<LexiconService>.Instance, h.Saga.Options, MemoryErasureTestKeys.Isolated());

        Guid lexiconId;

        if (dependentStore == MemoryEvaluationSource.Saga)
        {
            lexiconId = Require(await lexicon.UpsertAsync("Dependency", "fact", ["lexicon evidence"], LexiconScope.Global)).Id;

            await h.Saga.Store.InsertAsync("saga", "saga evidence", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", h.Saga.Embedding(), Ct);
        }
        else
        {
            await h.Saga.Store.InsertAsync("saga", "saga evidence", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", h.Saga.Embedding(), Ct);

            lexiconId = Require(await lexicon.UpsertAsync("Dependent", "fact", ["lexicon evidence"], LexiconScope.Global)).Id;
        }

        var sagaHead = (await h.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "saga", Ct))!;

        var lexiconHead = (await h.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Lexicon, lexiconId.ToString("N"), Ct))!;

        string dependent = dependentStore == MemoryEvaluationSource.Saga ? sagaHead.CurrentVersionId : lexiconHead.CurrentVersionId;

        string dependency = dependentStore == MemoryEvaluationSource.Saga ? lexiconHead.CurrentVersionId : sagaHead.CurrentVersionId;

        // Seed a lawful exact edge through the owning declarative constraints; the genuine reader
        // independently proves its backward sequence, target store, exact version and closure.
        await using (var edge = h.Connection.CreateCommand())
        {
            edge.CommandText = """
                INSERT INTO annal_dependencies(DependentVersionId,DependentSequence,DependencyVersionId,DependencySequence,RelationCode,Ordinal,CreatedAtUtc)
                SELECT dependent.VersionId,dependent.Sequence,dependency.VersionId,dependency.Sequence,2,1,$at
                FROM annal_versions dependent,annal_versions dependency
                WHERE dependent.VersionId = $dependent AND dependency.VersionId = $dependency
                """;

            edge.Parameters.AddWithValue("$dependent", dependent);

            edge.Parameters.AddWithValue("$dependency", dependency);

            UtcInstantSql.AddParameter(edge, "$at", DateTimeOffset.UtcNow);

            Assert.Equal(1, await edge.ExecuteNonQueryAsync());
        }

        await h.AdmitAsync();

        var lexiconVersion = Assert.Single(await h.Saga.Annals.GetVersionsAsync(lexiconHead.ClaimId, Ct));

        var request = dependentStore == MemoryEvaluationSource.Saga ? await h.RequestAsync("saga")
            : Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
            {
                Memory = new([], [], [new(lexiconId, lexiconVersion.VersionId, lexiconVersion.ContentHashFormat, new(lexiconVersion.ContentHash!))])
            }));

        var snapshot = Require(await h.Reader.ReadAsync(request, h.Invocation, h.Lease, Ct));

        Assert.Equal(2, snapshot.Coordinates.Length);

        Assert.Equal(dependentStore, Assert.Single(snapshot.Dependencies).Store);

        Assert.NotEqual(dependentStore, Assert.Single(snapshot.Coordinates, source => source.VersionId == dependency).Store);

        Assert.True((await h.Reader.ValidateAsync(snapshot, h.Lease, Ct)).IsSuccess);

        var registration = Registration(snapshot.Coordinates, snapshot.Dependencies) with
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceAsOfUtc = snapshot.AsOfUtc,
            Ownership = new(snapshot.Fence!.DatasetGeneration, MemoryEvaluationScopeKind.Global, null)
        };

        var store = new MemoryEvaluationEvidenceStore(new ConnectionSource(h.Connection));

        Require(await store.RegisterAsync(registration, snapshot.Coordinates, Ct));

        var prepared = Require(await store.GetAsync(registration.EvaluationId, Ct));

        Assert.Equal(snapshot.Coordinates.ToArray(), prepared.Sources.ToArray());

        Assert.Equal(snapshot.Dependencies.ToArray(), prepared.Registration.Dependencies.ToArray());

        var report = Report(registration, snapshot.Coordinates) with { CompletedAtUtc = registration.CreatedAtUtc.AddSeconds(1) };

        Require(await store.CompleteAsync(registration.EvaluationId, report, Ct));

        var terminal = Require(await store.GetAsync(registration.EvaluationId, Ct));

        Assert.Equal(snapshot.Coordinates.ToArray(), terminal.Report!.Sources.ToArray());

        Assert.Equal(snapshot.Dependencies.ToArray(), terminal.Report.Dependencies.ToArray());

        Assert.Equal(MemoryEvaluationDigests.Report(report), terminal.ReportDigest);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("covenant-lookalike")]
    [InlineData("dependent-store")]
    public async Task Annals_dependency_endpoints_require_unique_shared_version_binding(string invalid)
    {
        await using var h = await Harness.Create();

        var dependent = Source();

        var target = Source() with { Store = MemoryEvaluationSource.Lexicon, SubjectId = Guid.NewGuid().ToString("N"), ClaimId = "lexicon-claim", VersionId = Guid.NewGuid().ToString("D"), Role = MemoryEvaluationCoordinateRole.Dependency };

        ImmutableArray<MemoryEvaluationSourceCoordinate> sources = [dependent, target];

        if (invalid == "missing") sources = [dependent];

        if (invalid == "ambiguous") sources = [dependent, target, Source() with { SubjectId = "other-subject", ClaimId = "other-claim", VersionId = target.VersionId, Role = MemoryEvaluationCoordinateRole.Dependency }];

        if (invalid == "covenant-lookalike") sources = [dependent, new(MemoryEvaluationCoordinateRole.Dependency, MemoryEvaluationSource.Covenant, Guid.NewGuid().ToString("D"), null, target.VersionId, 1, Digest, null, null, MemoryEvaluationScopeKind.Global, null, null, null, null, null, MemoryEvaluationSourceLifecycle.Active, ContentSensitivity.CovenantDerived, null, null, Dataset, 1, CovenantLane.Confirmed, CovenantPlacement.GlobalConfirmed, CovenantOrigin.Operator)];

        ImmutableArray<MemoryEvaluationDependencyEdge> edges = [new(invalid == "dependent-store" ? MemoryEvaluationSource.Lexicon : MemoryEvaluationSource.Saga, dependent.VersionId, target.VersionId, AnnalDependencyRelation.DerivedFrom, 1)];

        var result = await h.Store.RegisterAsync(Registration(sources, edges), sources, Ct);

        Assert.True(result.IsFailure);

        Assert.Equal("MemoryEvaluation.InvalidMetadata", result.Error.Code);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluation_sources"));
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    public async Task Imported_opaque_identities_and_truthful_author_spelling_roundtrip(string authorFormat)
    {
        await using var h = await Harness.Create();

        var author = Guid.NewGuid();

        var source = Source() with { SubjectId = "a:b:mémoire", ClaimId = "historical:claim", VersionId = "c:版", SourceSessionId = author.ToString(authorFormat), CanonicalOwnerSessionId = Guid.NewGuid().ToString("D").ToUpperInvariant() };

        var r = Registration([source]);

        Require(await h.Store.RegisterAsync(r, [source], Ct));

        Assert.Equal(source, Assert.Single(Require(await h.Store.GetAsync(r.EvaluationId, Ct)).Sources));

        await using var key = h.Connection.CreateCommand();

        key.CommandText = "SELECT count(*) FROM memory_evaluations root,json_each(root.ErasureKeysJson) item WHERE item.value = 'session:' || $artifactKey";

        key.Parameters.AddWithValue("$artifactKey", author.ToString("N"));

        Assert.Equal(1L, await key.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Registration_replay_is_exact_and_conflicting_metadata_is_refused()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        Assert.True((await h.Store.RegisterAsync(r with { OperationId = Guid.NewGuid() }, [Source()], Ct)).IsFailure);

        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));
    }

    [Fact]
    public async Task Terminal_append_replays_exactly_and_refuses_replacement_or_erased_root()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        var report = Report(r, [Source()]);

        var stored = Require(await h.Store.CompleteAsync(r.EvaluationId, report, Ct));

        Assert.Equal(MemoryEvaluationDigests.Report(report), stored.ReportDigest);

        Assert.Equal(stored.ReportDigest, Require(await h.Store.CompleteAsync(r.EvaluationId, report, Ct)).ReportDigest);

        Assert.True((await h.Store.CompleteAsync(r.EvaluationId, report with { ElapsedMilliseconds = 1 }, Ct)).IsFailure);

        Require(await h.Store.DeleteAsync(new(r.EvaluationId, stored.ReportDigest!.Value, true), Ct));

        Assert.True((await h.Store.CompleteAsync(r.EvaluationId, report, Ct)).IsFailure);
    }

    [Fact]
    public async Task Cleanup_requires_confirmation_exact_digest_and_terminal_evidence()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        Assert.True((await h.Store.DeleteAsync(new(r.EvaluationId, Digest, true), Ct)).IsFailure);

        var stored = Require(await h.Store.CompleteAsync(r.EvaluationId, Report(r, [Source()]), Ct));

        Assert.True((await h.Store.DeleteAsync(new(r.EvaluationId, stored.ReportDigest!.Value, false), Ct)).IsFailure);

        Assert.True((await h.Store.DeleteAsync(new(r.EvaluationId, Digest, true), Ct)).IsFailure);

        Assert.True(Require(await h.Store.DeleteAsync(new(r.EvaluationId, stored.ReportDigest.Value, true), Ct)).Deleted);

        Assert.False(Require(await h.Store.DeleteAsync(new(r.EvaluationId, stored.ReportDigest.Value, true), Ct)).Deleted);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluation_sources"));

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluation_reports"));

        Assert.True((await h.Store.GetAsync(r.EvaluationId, Ct)).IsFailure);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("digest")]
    [InlineData("scope")]
    [InlineData("enum")]
    [InlineData("time")]
    [InlineData("owner")]
    [InlineData("manifest")]
    [InlineData("author")]
    [InlineData("validity")]
    [InlineData("overflow")]
    [InlineData("duplicate")]
    [InlineData("duplicate-role")]
    [InlineData("edge")]
    public async Task Unvalidated_metadata_is_refused_without_reserving_quota(string invalid)
    {
        await using var h = await Harness.Create();

        ImmutableArray<MemoryEvaluationSourceCoordinate> sources = [Source()];

        var r = Registration(sources);

        switch (invalid)
        {
            case "identity": sources = [Source() with { SubjectId = "invalid\nidentity" }]; break;

            case "digest": r = r with { WorkloadDigest = default }; break;

            case "scope": r = r with { Ownership = new(Dataset, MemoryEvaluationScopeKind.Campaign, null) }; break;

            case "enum": sources = [Source() with { Origin = (AnnalOrigin)100 }]; break;

            case "time": r = r with { SourceAsOfUtc = null }; break;

            case "owner": sources = [Source() with { CanonicalOwnerSessionId = Guid.NewGuid().ToString("N") }]; break;

            case "manifest": r = r with { ManifestDigest = Digest }; break;

            case "duplicate": sources = [Source(), Source() with { SourceSessionId = Guid.NewGuid().ToString("D").ToUpperInvariant() }]; break;

            case "author": sources = [Source() with { SourceSessionId = "not-a-session" }]; break;

            case "validity": sources = [Source() with { ValidToUtc = At.AddDays(-2) }]; break;

            case "overflow": sources = [Source() with { SubjectId = new string('x', MemoryEvaluationLimits.EvidenceBytes + 1) }]; break;

            case "duplicate-role": sources = [Source(), Source() with { Role = MemoryEvaluationCoordinateRole.Dependency, CanonicalOwnerSessionId = Guid.NewGuid().ToString("D").ToUpperInvariant() }]; break;

            case "edge": r = r with { Dependencies = [new(MemoryEvaluationSource.Saga, "version", "absent", AnnalDependencyRelation.Supersedes, 0)] }; break;
        }

        if (invalid != "manifest") r = r with { ManifestDigest = MemoryEvaluationDigests.SourceManifest(sources, r.Dependencies) };

        Assert.True((await h.Store.RegisterAsync(r, sources, Ct)).IsFailure);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));
    }

    [Fact]
    public async Task Coordinate_and_future_terminal_size_are_bounded_before_registration()
    {
        await using var h = await Harness.Create();

        var tooMany = Enumerable.Range(0, 129).Select(i => Source() with { VersionId = "v" + i }).ToImmutableArray();

        Assert.True((await h.Store.RegisterAsync(Registration(tooMany), tooMany, Ct)).IsFailure);

        var tooLarge = Enumerable.Range(0, 16).Select(i => Source() with { SubjectId = new string('a', 1000) + i, VersionId = new string('v', 1000) + i, ClaimId = new string('c', 1000) + i }).ToImmutableArray();

        Assert.True((await h.Store.RegisterAsync(Registration(tooLarge), tooLarge, Ct)).IsFailure);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));
    }

    [Fact]
    public async Task Every_admitted_large_registration_can_publish_its_bounded_terminal_projection()
    {
        await using var h = await Harness.Create();

        int admitted = 0;

        int refused = 0;

        foreach (int length in new[] { 1, 100, 300, 600, 1000 })
        {
            var sources = Enumerable.Range(0, 16).Select(index => Source() with { SubjectId = new string('s', length) + index, VersionId = "v" + index }).ToImmutableArray();

            var registration = Registration(sources) with { CheckCount = 16, PairCount = 16 };

            var prepared = await h.Store.RegisterAsync(registration, sources, Ct);

            if (prepared.IsFailure) { refused++; continue; }
            admitted++;

            var report = Report(registration, sources) with { Aggregate = new(0, 0, 0, 0, 0, 0, 0, Enumerable.Range(0, 16).Select(index => new MemoryEvaluationCheckAggregate(index, MemoryEvaluationCheckKind.JsonSchema, Digest, 0, 0, 0, 0)).ToImmutableArray(), Digest, MemoryEvaluationReason.Interrupted), InputTokens = long.MaxValue, OutputTokens = long.MaxValue, Cost = decimal.MaxValue, ElapsedMilliseconds = long.MaxValue };

            var stored = Require(await h.Store.CompleteAsync(registration.EvaluationId, report, Ct));

            Assert.InRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(stored, RetroDownfall.Arcanum.Core.Serialization.GrimoireJsonContext.Default.MemoryEvaluationStoredEvidence).Length, 1, MemoryEvaluationLimits.EvidenceBytes);

            Assert.True((long)(await h.Scalar("SELECT max(PreparedBytes) FROM memory_evaluations"))! <= MemoryEvaluationLimits.EvidenceBytes - 8192);
        }
        Assert.True(admitted > 0);

        Assert.True(refused > 0);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("oversize")]
    [InlineData("contradiction")]
    public async Task Terminal_rejects_invalid_numeric_or_contradictory_check_aggregates(string invalid)
    {
        await using var h = await Harness.Create();

        var registration = Registration([Source()]);

        Require(await h.Store.RegisterAsync(registration, [Source()], Ct));

        var report = Report(registration, [Source()]);

        if (invalid == "contradiction") report = report with { State = MemoryEvaluationState.Completed, Decision = new(MemoryEvaluationOutcome.Benefit, MemoryEvaluationReason.None), Aggregate = new(3, 3, 0, 3, 0, 0, 0, [new(0, MemoryEvaluationCheckKind.Regex, Digest, 0, 0, 3, 0)], Digest) };
        else report = report with { State = MemoryEvaluationState.Completed, Decision = new(MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.InvalidEvaluation), Aggregate = report.Aggregate with { CompletedPairs = invalid == "negative" ? -1 : 17, FailureReason = MemoryEvaluationReason.InvalidEvaluation } };

        Assert.True((await h.Store.CompleteAsync(registration.EvaluationId, report, Ct)).IsFailure);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluation_reports"));
    }

    [Theory]
    [InlineData("evaluation", true)]
    [InlineData("evaluation", false)]
    [InlineData("operation", true)]
    [InlineData("operation", false)]
    [InlineData("terminal", true)]
    [InlineData("terminal", false)]
    public async Task Insert_replacement_cannot_overwrite_retained_evaluation_operation_or_terminal(string conflict, bool foreignKeys)
    {
        await using var h = await Harness.Create();
        var registration = Registration([Source()]);
        Require(await h.Store.RegisterAsync(registration, [Source()], Ct));
        var before = Require(await h.Store.CompleteAsync(registration.EvaluationId, Report(registration, [Source()]), Ct));
        var root = await h.Scalar("SELECT RegistrationJson FROM memory_evaluations");
        var coordinate = await h.Scalar("SELECT CoordinateJson FROM memory_evaluation_sources");
        await h.Execute(foreignKeys ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF");
        await using (var transaction = h.Connection.BeginTransaction())
        {
            await using var command = h.Connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$newId", Guid.NewGuid().ToString("N"));
            if (conflict == "operation")
            {
                command.CommandText = "INSERT INTO memory_evaluation_sources SELECT $newId,Ordinal,StoreCode,RoleCode,SubjectId,VersionId,CoordinateJson FROM memory_evaluation_sources";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            command.CommandText = conflict == "terminal"
                ? "INSERT OR REPLACE INTO memory_evaluation_reports SELECT EvaluationId,CompletedAtUtc,StateCode,OutcomeCode,ReasonCode,AggregateJson,InputTokens,OutputTokens,Cost,ElapsedMilliseconds+1,ReportDigest FROM memory_evaluation_reports"
                : $"INSERT OR REPLACE INTO memory_evaluations SELECT {(conflict == "operation" ? "$newId" : "EvaluationId")},OperationId,DatasetGeneration,ScopeCode,CampaignId,CreatedAtUtc,SourceAsOfUtc,SourceCount,json_set(RegistrationJson,'$.pairCount',4),ErasureKeysJson,PreparedBytes FROM memory_evaluations";
            var failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(19, failure.SqliteErrorCode);
            await transaction.RollbackAsync();
        }
        Assert.Equal(root, await h.Scalar("SELECT RegistrationJson FROM memory_evaluations"));
        Assert.Equal(coordinate, await h.Scalar("SELECT CoordinateJson FROM memory_evaluation_sources"));
        foreach (string table in new[] { "memory_evaluations", "memory_evaluation_sources", "memory_evaluation_reports" }) Assert.Equal(1L, await h.Scalar($"SELECT count(*) FROM {table}"));
        var after = Require(await h.Store.GetAsync(registration.EvaluationId, Ct));
        Assert.Equal(before.ReportDigest, after.ReportDigest);
        Assert.Equal(before.Report!.ElapsedMilliseconds, after.Report!.ElapsedMilliseconds);
        Assert.Equal(before.Sources.ToArray(), after.Sources.ToArray());
        Require(await h.Store.RegisterAsync(registration, [Source()], Ct));
        Require(await h.Store.CompleteAsync(registration.EvaluationId, Report(registration, [Source()]), Ct));
    }

    [Fact]
    public async Task Root_sources_and_terminal_are_immutable_even_through_raw_sql()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        Require(await h.Store.CompleteAsync(r.EvaluationId, Report(r, [Source()]), Ct));

        foreach (string table in new[] { "memory_evaluations", "memory_evaluation_sources", "memory_evaluation_reports" })
        {
            var exception = await Assert.ThrowsAsync<SqliteException>(() => h.Execute($"UPDATE {table} SET EvaluationId = EvaluationId"));

            Assert.Equal(19, exception.SqliteErrorCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Root_erasure_keys_survive_child_deletion_and_select_the_whole_evidence_set(bool foreignKeys)
    {
        await using var h = await Harness.Create();

        var owner = Guid.NewGuid();

        var source = Source() with { CanonicalOwnerSessionId = owner.ToString("D").ToUpperInvariant(), SourceSessionId = null };

        var registration = Registration([source]);

        Require(await h.Store.RegisterAsync(registration, [source], Ct));

        Require(await h.Store.CompleteAsync(registration.EvaluationId, Report(registration, [source]), Ct));

        if (!foreignKeys) await h.Execute("PRAGMA foreign_keys = OFF");

        // This is the static artifact-plan grammar: one normalized $artifactKey, root-last ordering.
        const string selection = "SELECT root.EvaluationId FROM memory_evaluations root,json_each(root.ErasureKeysJson) key WHERE key.value = 'session:' || $artifactKey";

        foreach (string table in new[] { "memory_evaluation_sources", "memory_evaluation_reports", "memory_evaluations" })
        {
            await using var command = h.Connection.CreateCommand();

            command.CommandText = "DELETE FROM " + table + " WHERE EvaluationId IN (" + selection + ")";

            command.Parameters.AddWithValue("$artifactKey", owner.ToString("N"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.True((await h.Store.CompleteAsync(registration.EvaluationId, Report(registration, [source]), Ct)).IsFailure);
    }

    [Fact]
    public async Task Generated_root_erasure_lookup_is_bound_to_the_retained_manifest()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        // Simulate imported corruption by bypassing the immutable guard; reads must fail closed.
        await h.Execute("DROP TRIGGER memory_evaluations_guard_update");

        await h.Execute("UPDATE memory_evaluations SET ErasureKeysJson = '[]'");

        Assert.True((await h.Store.GetAsync(r.EvaluationId, Ct)).IsFailure);
    }

    [Fact]
    public async Task Malformed_utf8_is_invalid_metadata_without_any_write()
    {
        await using var h = await Harness.Create();

        var source = Source() with { SubjectId = "bad\uD800" };

        var result = await h.Store.RegisterAsync(Registration([Source()]), [source], Ct);

        Assert.True(result.IsFailure);

        Assert.Equal("MemoryEvaluation.InvalidMetadata", result.Error.Code);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));
    }

    [Fact]
    public async Task A_sealed_manifest_refuses_later_appends_and_missing_roots_refuse_raw_terminals()
    {
        await using var h = await Harness.Create();

        var r = Registration([Source()]);

        Require(await h.Store.RegisterAsync(r, [Source()], Ct));

        var exception = await Assert.ThrowsAsync<SqliteException>(() => h.Execute("INSERT INTO memory_evaluation_sources SELECT EvaluationId,1,StoreCode,2,SubjectId,'later',CoordinateJson FROM memory_evaluation_sources"));

        Assert.Equal(19, exception.SqliteErrorCode);

        Require(await h.Store.CompleteAsync(r.EvaluationId, Report(r, [Source()]), Ct));

        await h.Execute("PRAGMA foreign_keys = OFF");

        await h.Execute("DELETE FROM memory_evaluations");

        await h.Execute("DELETE FROM memory_evaluation_reports");

        exception = await Assert.ThrowsAsync<SqliteException>(() => h.Execute("INSERT INTO memory_evaluation_reports(EvaluationId,CompletedAtUtc,StateCode,OutcomeCode,ReasonCode,AggregateJson,ElapsedMilliseconds,ReportDigest) SELECT EvaluationId,'2026-10-18T12:00:01.0000000Z',4,4,3,'{}',0,zeroblob(32) FROM memory_evaluation_sources"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Metadata_sink_and_sourcegen_inventory_have_no_transient_execution_authority_or_plaintext_shapes()
    {
        var consumer = Assert.Single(CovenantDerivedOutputInventory.Consumers, item => item.OwningTypeName == typeof(MemoryEvaluationEvidenceStore).FullName);

        Assert.Equal(DerivedOutputPolicy.ContentFreeMetadataOnly, consumer.Policy);

        Assert.Contains("opaque", consumer.Rationale);

        Type[] transient = [typeof(MemoryEvaluationValidatedRequest), typeof(MemoryEvaluationSourceSnapshot), typeof(MemoryEvaluationPromptLayout), typeof(MemoryEvaluationPromptPair), typeof(MemoryEvaluationExecutionPlan), typeof(MemoryEvaluationSendEvidence), typeof(MemoryEvaluationUsage), typeof(MemoryEvaluationArmResult), typeof(MemoryEvaluationRequest), typeof(MemoryEvaluationWorkload), typeof(MemoryEvaluationCheck)];

        foreach (Type type in transient)
        {
            Assert.Null(RetroDownfall.Arcanum.Core.Serialization.GrimoireJsonContext.Default.GetTypeInfo(type));

            Assert.DoesNotContain(typeof(IMemoryEvaluationEvidenceStore).GetMethods().SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType), accepted => accepted == type);
        }
        Assert.NotNull(RetroDownfall.Arcanum.Core.Serialization.GrimoireJsonContext.Default.GetTypeInfo(typeof(MemoryEvaluationCheckpoint)));
    }

    [Fact]
    public async Task Source_insert_failure_rolls_back_root_and_quota()
    {
        await using var h = await Harness.Create();

        await h.Execute("CREATE TEMP TRIGGER fail_source BEFORE INSERT ON memory_evaluation_sources WHEN NEW.Ordinal = 1 BEGIN SELECT RAISE(ABORT, 'injected'); END;");

        ImmutableArray<MemoryEvaluationSourceCoordinate> sources = [Source(), Source() with { SubjectId = "other", VersionId = "other-version" }];

        var r = Registration(sources);

        Assert.True((await h.Store.RegisterAsync(r, sources, Ct)).IsFailure);

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));

        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM memory_evaluation_sources"));
    }

    [Fact]
    public async Task Atomic_quota_counts_prepared_roots_and_exact_replay_precedes_quota()
    {
        await using var h = await Harness.Create();

        MemoryEvaluationRegistration? first = null;

        for (int i = 0; i < 1023; i++)
        {
            var r = Registration([Source()]);

            first ??= r;

            Require(await h.Store.RegisterAsync(r, [Source()], Ct));
        }

        await using var other = await h.OtherConnection();

        var secondStore = new MemoryEvaluationEvidenceStore(new ConnectionSource(other));

        var candidates = new[] { Registration([Source()]), Registration([Source()]) };

        var results = await Task.WhenAll(Task.Run(() => h.Store.RegisterAsync(candidates[0], [Source()], Ct)), Task.Run(() => secondStore.RegisterAsync(candidates[1], [Source()], Ct)));

        Assert.Single(results, result => result.IsSuccess);

        Assert.Single(results, result => result.IsFailure);

        Assert.Equal(1024L, await h.Scalar("SELECT count(*) FROM memory_evaluations"));

        Require(await h.Store.RegisterAsync(first!, [Source()], Ct));
    }

    internal static MemoryEvaluationSourceCoordinate Source() => new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Saga, "subject", "claim", "version", 1, Digest, null, AnnalOrigin.OperatorStated, MemoryEvaluationScopeKind.Global, null, At.AddDays(-1), null, At.AddDays(-1), null, MemoryEvaluationSourceLifecycle.Active, ContentSensitivity.None, null, null);

    internal static MemoryEvaluationRegistration Registration(ImmutableArray<MemoryEvaluationSourceCoordinate> sources, ImmutableArray<MemoryEvaluationDependencyEdge> edges = default) => new(Guid.NewGuid(), Guid.NewGuid(), At, new(Dataset, MemoryEvaluationScopeKind.Global, null), Digest, Digest, Digest, Digest, Digest, Digest, Digest, Digest, MemoryEvaluationDigests.SourceManifest(sources, edges), 3, 1, Dependencies: edges, SourceAsOfUtc: At.AddSeconds(-1));

    internal static MemoryEvaluationTerminalReport Report(MemoryEvaluationRegistration r, ImmutableArray<MemoryEvaluationSourceCoordinate> sources) => new(r, At.AddSeconds(1), MemoryEvaluationState.Interrupted, new(MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.Interrupted), new(0, 0, 0, 0, 0, 0, 0, [new(0, MemoryEvaluationCheckKind.Regex, Digest, 0, 0, 0, 0)], Digest, MemoryEvaluationReason.Interrupted), sources, null, null, null, 0, r.Dependencies);

    private static T Require<T>(Result<T> result) { Assert.True(result.IsSuccess, result.Error.ToString()); return result.Value; }

    private sealed class ConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);
    }

    private sealed class Harness(string path, string connectionString, SqliteConnection connection) : IAsyncDisposable
    {
        internal MemoryEvaluationEvidenceStore Store { get; } = new(new ConnectionSource(connection));

        internal SqliteConnection Connection => connection;

        internal static async Task<Harness> Create()
        {
            SqliteNativeRuntime.Instance.Initialize();

            string path = Path.Combine(Path.GetTempPath(), "memory-evidence-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);

            string cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "evidence.db"), Password = "memory-evaluation-test-key", Pooling = false }.ToString();

            var connection = await GrimoireSchemaTestInstaller.OpenAsync(cs, Ct);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, (await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, Ct)).Core.Health);

            return new(path, cs, connection);
        }

        internal Task<SqliteConnection> OtherConnection() => GrimoireSchemaTestInstaller.OpenAsync(connectionString, Ct);

        internal async Task<object?> Scalar(string sql) { await using var c = connection.CreateCommand(); c.CommandText = sql; return await c.ExecuteScalarAsync(); }
        internal async Task Execute(string sql) { await using var c = connection.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync() { await connection.DisposeAsync(); Directory.Delete(path, true); }
    }
}
