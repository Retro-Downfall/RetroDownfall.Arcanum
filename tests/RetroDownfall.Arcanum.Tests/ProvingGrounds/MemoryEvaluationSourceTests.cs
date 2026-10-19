using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationSourceTests
{
    [SkippableFact]
    public async Task Corrected_saga_preserves_owner_session_when_version_author_is_null()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid session = await harness.Saga.SessionBoundToNewCampaignAsync();

        await harness.Saga.Store.InsertAsync("corrected", "old fact", DateTimeOffset.UtcNow.AddMinutes(-5), session, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.CorrectAsync("corrected", AnnalContentDigest.ForSagaMemory("old fact"), "correct fact", harness.Saga.Embedding(), DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        await using SqliteCommand campaign = harness.Connection.CreateCommand();

        campaign.CommandText = "SELECT CampaignId FROM saga_memories WHERE Id = 'corrected'";

        await harness.AdmitAsync(Guid.Parse((string)(await campaign.ExecuteScalarAsync())!));

        var request = await harness.RequestAsync("corrected");

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None));

        var source = Assert.Single(snapshot.Coordinates.Where(source => source.Role == MemoryEvaluationCoordinateRole.Intervention));

        Assert.Equal(session.ToString("D").ToUpperInvariant(), source.CanonicalOwnerSessionId);

        Assert.Null(source.SourceSessionId);

        Assert.Equal(AnnalOrigin.OperatorStated, source.Origin);

        Assert.Contains("correct fact", Assert.Single(snapshot.Fragments).Text);

        Assert.Null(harness.Invocation.Campaign!.Value.RootIdentityDigest);
    }

    [SkippableFact]
    public async Task An_authority_free_invocation_cannot_borrow_a_live_turn()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var result = await harness.Reader.ReadAsync(await harness.RequestAsync("source"), ArcanumInvocationContext.None, harness.Lease, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [SkippableFact]
    public async Task Historical_dependency_keeps_corrected_owner_and_null_author_without_injecting_old_text()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid session = await harness.Saga.SessionBoundToNewCampaignAsync();

        await harness.Saga.Store.InsertAsync("history", "original secret", DateTimeOffset.UtcNow.AddMinutes(-5), session, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.CorrectAsync("history", AnnalContentDigest.ForSagaMemory("original secret"), "first correction", harness.Saga.Embedding(), DateTimeOffset.UtcNow.AddMinutes(-3), CancellationToken.None);

        var first = await harness.RequestAsync("history");

        await harness.Saga.Store.CorrectAsync("history", AnnalContentDigest.ForSagaMemory("first correction"), "current fact", harness.Saga.Embedding(), DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        await using SqliteCommand campaign = harness.Connection.CreateCommand();

        campaign.CommandText = "SELECT CampaignId FROM saga_memories WHERE Id = 'history'";

        await harness.AdmitAsync(Guid.Parse((string)(await campaign.ExecuteScalarAsync())!));

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(await harness.RequestAsync("history"), harness.Invocation, harness.Lease, CancellationToken.None));

        var dependency = Assert.Single(snapshot.Coordinates.Where(source => source.VersionId == first.Request.Memory.Saga[0].VersionId));

        Assert.Equal(MemoryEvaluationCoordinateRole.Dependency, dependency.Role);

        Assert.Equal(session.ToString("D").ToUpperInvariant(), dependency.CanonicalOwnerSessionId);

        Assert.Null(dependency.SourceSessionId);

        Assert.Equal(3, snapshot.Coordinates.Length);

        Assert.Equal(2, snapshot.Dependencies.Length);

        Assert.DoesNotContain("original secret", snapshot.RenderedMemory);

        Assert.DoesNotContain("first correction", snapshot.RenderedMemory);
    }

    [SkippableFact]
    public async Task Reads_and_metadata_revalidation_write_nothing_and_detect_a_current_source_change()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        await using SqliteCommand changes = harness.Connection.CreateCommand();

        changes.CommandText = "SELECT total_changes()";

        var before = await changes.ExecuteScalarAsync();

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(await harness.RequestAsync("source"), harness.Invocation, harness.Lease, CancellationToken.None));

        Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsSuccess);

        Assert.Equal(before, await changes.ExecuteScalarAsync());

        await harness.Saga.Store.CorrectAsync("source", AnnalContentDigest.ForSagaMemory("fact"), "new fact", harness.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Released_turn_cannot_read_or_revalidate_sources()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var request = await harness.RequestAsync("source");

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None));

        await harness.Lease.DisposeAsync();

        Assert.True((await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);

        Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Current_protected_label_cannot_be_laundered_by_clean_annals_sensitivity()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid id = Guid.NewGuid();

        await harness.Saga.Store.InsertAsync(id.ToString(), "protected secret", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.LabelSensitiveAsync(id);

        await harness.AdmitAsync();

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(await harness.RequestAsync(id.ToString()), harness.Invocation, harness.Lease, CancellationToken.None));

        Assert.Equal(ContentSensitivity.CovenantDerived, Assert.Single(snapshot.Coordinates).Sensitivity);

        Assert.False(snapshot.Provenance!.IsEmpty);

        Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsSuccess);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_lexicon_uses_structured_hash_and_respects_unselected_campaign_shadow(bool shadow)
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        LexiconService lexicon = new(harness.Saga.Context, NullLogger<LexiconService>.Instance, harness.Saga.Options, MemoryErasureTestKeys.Isolated());

        var entry = MemoryEvaluationSourceHarness.Require(await lexicon.UpsertAsync("Compiler", "tool", ["structured fact"], LexiconScope.Global));

        Guid campaign = CovenantOperationGateFixture.CampaignOne;

        if (shadow)
        {
            MemoryEvaluationSourceHarness.Require(await lexicon.UpsertAsync("Compiler", "tool", ["unselected shadow"], LexiconScope.ForCampaign(campaign)));
        }

        await harness.AdmitAsync(campaign);

        var head = (await harness.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Lexicon, entry.Id.ToString("N"), CancellationToken.None))!;

        var version = Assert.Single(await harness.Saga.Annals.GetVersionsAsync(head.ClaimId, CancellationToken.None));

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Scope = new(MemoryEvaluationScopeKind.Campaign, campaign),
            Memory = new([], [], [new(entry.Id, version.VersionId, version.ContentHashFormat, new(version.ContentHash!))])
        }));

        var result = await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None);

        if (shadow)
        {
            Assert.True(result.IsFailure);
        }
        else
        {
            var snapshot = MemoryEvaluationSourceHarness.Require(result);

            Assert.Contains("structured fact", snapshot.RenderedMemory);

            Assert.Null(Assert.Single(snapshot.Coordinates).CanonicalOwnerSessionId);

            Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsSuccess);
        }
    }

    [Fact]
    public async Task Selected_covenant_without_optional_store_refuses_before_any_connection_access()
    {
        var gate = CovenantOperationGateFixture.CreateGate();

        await using var lease = MemoryEvaluationSourceHarness.Require(await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None));

        var invocation = MemoryEvaluationSourceHarness.Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
            CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), lease.Snapshot.RuntimeAuthorityGeneration, lease.Snapshot.AuthorityEpoch)));

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Memory = new([new(Guid.NewGuid(), Guid.NewGuid(), new CovenantDigest(new byte[32]))], [], [])
        }));

        Assert.True((await gate.ValidateTurnLeaseAsync(lease, CancellationToken.None)).IsSuccess);

        Assert.True(invocation.CanReadCovenant);

        UnusedConnectionSource connections = new();

        MemoryEvaluationSourceReader reader = new(connections, gate, TimeProvider.System);

        var result = await reader.ReadAsync(request, invocation, lease, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.MemoryEvaluation.StaleSource, result.Error.Code);

        Assert.Equal(0, connections.CoreCalls);

        Assert.Equal(0, connections.CanonicalCalls);
    }

    [Fact]
    public async Task Covenant_full_linker_never_returns_unselected_plaintext()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        var global = await fixture.SeedHeadAsync(CovenantScope.Global, null, "selected", CovenantLane.Confirmed, CovenantOperation.Set, "selected context", CancellationToken.None);

        await fixture.SeedHeadAsync(CovenantScope.Global, null, "other", CovenantLane.Confirmed, CovenantOperation.Set, "UNSELECTED_SENTINEL", CancellationToken.None);

        FakeCovenantAvailability availability = new();

        availability.Mutate(value => value with { DatasetGeneration = fixture.ReadDatasetGenerationAsync(CancellationToken.None).GetAwaiter().GetResult() });

        var gate = CovenantOperationGateFixture.CreateGate(availability: availability);

        await using var lease = MemoryEvaluationSourceHarness.Require(await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None));

        var invocation = MemoryEvaluationSourceHarness.Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
            CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), lease.Snapshot.RuntimeAuthorityGeneration, lease.Snapshot.AuthorityEpoch)));

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Memory = new([new(global.EntryId, global.VersionId, global.Compiled!.FragmentHash)], [], [])
        }));

        MemoryEvaluationSourceReader reader = new(new FixedCovenantConnectionSource(fixture.Connection), gate, TimeProvider.System, fixture.Store);

        var snapshot = MemoryEvaluationSourceHarness.Require(await reader.ReadAsync(request, invocation, lease, CancellationToken.None));

        Assert.Contains("selected context", snapshot.RenderedMemory);

        Assert.DoesNotContain("UNSELECTED_SENTINEL", snapshot.RenderedMemory);

        Assert.Single(snapshot.Fragments);

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? state, string sql) => statements.Add(sql), null);

        try
        {
            Assert.True((await reader.ValidateAsync(snapshot, lease, CancellationToken.None)).IsSuccess);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        string metadataRead = Assert.Single(statements, sql => sql.Contains("FROM covenant_state st", StringComparison.Ordinal));

        Assert.Equal(2, metadataRead.Split("length(CAST(v.CompiledContent AS BLOB)) AS CompiledContent", StringSplitOptions.None).Length - 1);

        foreach (string statement in statements)
        {
            string withoutLengths = statement.Replace("length(CAST(v.CompiledContent AS BLOB)) AS CompiledContent", "", StringComparison.Ordinal);

            Assert.DoesNotContain("CompiledContent", withoutLengths, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True((await reader.ValidateAsync(snapshot with { Coordinates = [snapshot.Coordinates[0] with { ContentDigest = new CovenantDigest(new byte[32]) }] }, lease, CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("one-missing")]
    [InlineData("extra")]
    public void Covenant_metadata_projection_refuses_unrecognized_content_projection(string change)
    {
        const string expected = "v.CompiledContent AS CompiledContent";

        string sql = CovenantStoreSql.TurnSnapshot();

        sql = change switch
        {
            "renamed" => sql.Replace(expected, "v.CompiledContent AS Payload", StringComparison.Ordinal),
            "one-missing" => sql.Remove(sql.IndexOf(expected, StringComparison.Ordinal), expected.Length),
            _ => sql + " SELECT v.compiledcontent AS ExtraPayload"
        };

        Assert.Throws<InvalidOperationException>(() => MemoryEvaluationSourceReader.CovenantMetadataProjection(sql));
    }

    [SkippableTheory]
    [InlineData("stale")]
    [InlineData("hash")]
    [InlineData("retired")]
    [InlineData("erased")]
    [InlineData("future")]
    [InlineData("unresolved")]
    [InlineData("wrong-scope")]
    public async Task Ineligible_saga_targets_are_refused_without_fallback(string condition)
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid? session = condition == "unresolved" ? await harness.Saga.SessionWithUnresolvedBindingAsync()
            : condition == "wrong-scope" ? await harness.Saga.SessionBoundToNewCampaignAsync() : null;

        await harness.Saga.Store.InsertAsync("source", "fact", condition == "future" ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow.AddMinutes(-1),
            session, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var request = await harness.RequestAsync("source");

        if (condition == "stale")
        {
            await harness.Saga.Store.CorrectAsync("source", AnnalContentDigest.ForSagaMemory("fact"), "new fact", harness.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None);
        }
        else if (condition == "retired")
        {
            await harness.Saga.Store.RetireAsync("source", AnnalContentDigest.ForSagaMemory("fact"), DateTimeOffset.UtcNow, CancellationToken.None);
        }
        else if (condition == "erased")
        {
            await harness.Saga.Store.DeleteAsync("source", CancellationToken.None);
        }
        else if (condition == "hash")
        {
            request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(request.Request with
            {
                Memory = new([], [request.Request.Memory.Saga[0] with { ContentDigest = new CovenantDigest(new byte[32]) }], [])
            }));
        }

        Assert.True((await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Claimless_source_is_refused_without_backfill()
    {
        await using MemoryEvaluationSourceHarness harness = new(await SagaStoreHarness.CreateAsync());

        await harness.Saga.Store.InsertAsync("claimless", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Memory = new([], [new("claimless", "absent", AnnalContentHashFormat.LegacyStoreDigest, new(AnnalContentDigest.ForSagaMemory("fact")))], [])
        }));

        Assert.True((await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);

        Assert.Null(await harness.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "claimless", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Correction_with_missing_predecessor_edge_is_refused()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "old", DateTimeOffset.UtcNow.AddMinutes(-2), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.CorrectAsync("source", AnnalContentDigest.ForSagaMemory("old"), "new", harness.Saga.Embedding(), DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        await using SqliteCommand corrupt = harness.Connection.CreateCommand();

        corrupt.CommandText = "DELETE FROM annal_dependencies";

        await corrupt.ExecuteNonQueryAsync();

        await harness.AdmitAsync();

        Assert.True((await harness.Reader.ReadAsync(await harness.RequestAsync("source"), harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableTheory]
    [InlineData("digest")]
    [InlineData("alias")]
    public async Task Unproved_protected_label_is_refused(string corruption)
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid id = Guid.NewGuid();

        await harness.Saga.Store.InsertAsync(id.ToString(), "secret", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.LabelSensitiveAsync(id);

        await using SqliteCommand corrupt = harness.Connection.CreateCommand();

        corrupt.CommandText = "DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET "
            + (corruption == "digest" ? "ArtifactLabelDigest = zeroblob(32)" : "ArtifactId = lower(ArtifactId)");

        await corrupt.ExecuteNonQueryAsync();

        await harness.AdmitAsync();

        Assert.True((await harness.Reader.ReadAsync(await harness.RequestAsync(id.ToString()), harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Oversized_dependency_closure_is_refused_instead_of_truncated()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("chain", "value 0", DateTimeOffset.UtcNow.AddMinutes(-10), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        for (int index = 1; index <= 128; index++)
        {
            await harness.Saga.Store.CorrectAsync("chain", AnnalContentDigest.ForSagaMemory($"value {index - 1}"), $"value {index}", harness.Saga.Embedding(),
                DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(index), CancellationToken.None);
        }

        await harness.AdmitAsync();

        Assert.True((await harness.Reader.ReadAsync(await harness.RequestAsync("chain"), harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Global_covenant_is_refused_when_an_unselected_campaign_head_shadows_it()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        Guid campaign = CovenantOperationGateFixture.CampaignOne;

        await fixture.AddCampaignAsync(campaign, "shadow", CancellationToken.None);

        var global = await fixture.SeedHeadAsync(CovenantScope.Global, null, "selected", CovenantLane.Confirmed, CovenantOperation.Set, "global", CancellationToken.None);

        await fixture.SeedHeadAsync(CovenantScope.Campaign, campaign, "selected", CovenantLane.Confirmed, CovenantOperation.Set, "shadow", CancellationToken.None);

        FakeCovenantAvailability availability = new();

        Guid dataset = await fixture.ReadDatasetGenerationAsync(CancellationToken.None);

        availability.Mutate(value => value with { DatasetGeneration = dataset });

        var gate = CovenantOperationGateFixture.CreateGate(availability: availability);

        var context = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaign), 1, 1, null, null);

        await using var lease = MemoryEvaluationSourceHarness.Require(await gate.AcquireTurnAsync(context, CancellationToken.None));

        var invocation = MemoryEvaluationSourceHarness.Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
            context, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), lease.Snapshot.RuntimeAuthorityGeneration, lease.Snapshot.AuthorityEpoch)));

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Scope = new(MemoryEvaluationScopeKind.Campaign, campaign),
            Memory = new([new(global.EntryId, global.VersionId, global.Compiled!.FragmentHash)], [], [])
        }));

        MemoryEvaluationSourceReader reader = new(new FixedCovenantConnectionSource(fixture.Connection), gate, TimeProvider.System, fixture.Store);

        Assert.True((await reader.ReadAsync(request, invocation, lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Pending_owner_deletion_invalidates_a_snapshot_even_while_the_subject_row_remains()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        Guid session = await harness.Saga.SessionBoundToNewCampaignAsync();

        await harness.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), session, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await using SqliteCommand command = harness.Connection.CreateCommand();

        command.CommandText = "SELECT CampaignId FROM saga_memories WHERE Id = 'source'";

        await harness.AdmitAsync(Guid.Parse((string)(await command.ExecuteScalarAsync())!));

        var request = await harness.RequestAsync("source");

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None));

        command.CommandText = "INSERT INTO owner_deletion_events (OwnerKindCode, OwnerId, DeletedAtUtc) VALUES (2, $owner, '2026-10-18T00:00:00.0000000Z')";

        command.Parameters.AddWithValue("$owner", session.ToString("D").ToUpperInvariant());

        await command.ExecuteNonQueryAsync();

        Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsFailure);

        Assert.True((await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Metadata_revalidation_does_not_select_source_plaintext()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var snapshot = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(await harness.RequestAsync("source"), harness.Invocation, harness.Lease, CancellationToken.None));

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(harness.Connection.Handle, (object? state, string sql) => statements.Add(sql), null);

        try
        {
            Assert.True((await harness.Reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsSuccess);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(harness.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.NotEmpty(statements);

        foreach (string statement in statements)
        {
            string withoutLengths = System.Text.RegularExpressions.Regex.Replace(statement, @"length\(CAST\([^)]*AS BLOB\)\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            Assert.DoesNotMatch(@"(?i)\b(Content|FactsJson|FactsText|CompiledContent)\b", withoutLengths);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_source_suppression_respects_the_existing_pin_override(bool pinned)
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("source", "same fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.InsertAsync("survivor", "same fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        var source = (await harness.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "source", CancellationToken.None))!;

        var survivor = (await harness.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "survivor", CancellationToken.None))!;

        await using SqliteCommand suppression = harness.Connection.CreateCommand();

        suppression.CommandText = """
            INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
            VALUES ('test-receipt', 1, 1, 1, 0, $hash, $hash, 'survivor', $survivor, '2026-10-01T00:00:00.0000000Z');
            INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            VALUES ('test-receipt', 1, 'source', $source, 1, $content, $hash, '{}');
            INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            VALUES ('test-receipt', 2, 'survivor', $survivor, 1, $content, $hash, '{}');
            INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, 'test-receipt');
            """;

        suppression.Parameters.AddWithValue("$hash", new string('A', 64));

        suppression.Parameters.AddWithValue("$content", AnnalContentDigest.ForSagaMemory("same fact"));

        suppression.Parameters.AddWithValue("$source", source.CurrentVersionId);

        suppression.Parameters.AddWithValue("$survivor", survivor.CurrentVersionId);

        await suppression.ExecuteNonQueryAsync();

        if (pinned)
        {
            await harness.Saga.Store.SetPinAsync("source", true, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await harness.AdmitAsync();

        Assert.Equal(pinned, (await harness.Reader.ReadAsync(await harness.RequestAsync("source"), harness.Invocation, harness.Lease, CancellationToken.None)).IsSuccess);
    }

    [SkippableFact]
    public async Task Oversized_exact_metadata_is_refused_even_when_source_text_is_small()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        string id = new('a', MemoryEvaluationLimits.EvidenceBytes + 1);

        await harness.Saga.Store.InsertAsync(id, "fact", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        Assert.True((await harness.Reader.ReadAsync(await harness.RequestAsync(id), harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task Permuting_an_explicit_target_set_preserves_prepared_prompt_bytes()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("a", "first", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.InsertAsync("b", "second", DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var a = (await harness.RequestAsync("a")).Request.Memory.Saga[0];

        var b = (await harness.RequestAsync("b")).Request.Memory.Saga[0];

        var forward = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Memory = new([], [a, b], []) }));

        var reversed = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(forward.Request with { Memory = new([], [b, a], []) }));

        var left = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(forward, harness.Invocation, harness.Lease, CancellationToken.None));

        var right = MemoryEvaluationSourceHarness.Require(await harness.Reader.ReadAsync(reversed, harness.Invocation, harness.Lease, CancellationToken.None));

        Assert.Equal(left.RenderedMemory, right.RenderedMemory);

        Assert.Equal(left.ManifestDigest, right.ManifestDigest);

        Assert.Equal(left.Coordinates.ToArray(), right.Coordinates.ToArray());
    }

    [SkippableFact]
    public async Task Remaining_memory_budget_is_measured_before_loading_another_source()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        await harness.Saga.Store.InsertAsync("a", new string('a', 20 * 1024), DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.InsertAsync("b", new string('b', 20 * 1024), DateTimeOffset.UtcNow.AddMinutes(-1), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var a = (await harness.RequestAsync("a")).Request.Memory.Saga[0];

        var b = (await harness.RequestAsync("b")).Request.Memory.Saga[0];

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Memory = new([], [a, b], []) }));

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(harness.Connection.Handle, (object? state, string sql) => statements.Add(sql), null);

        try
        {
            Assert.True((await harness.Reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None)).IsFailure);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(harness.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Single(statements, sql => sql.StartsWith("SELECT Content FROM saga_memories", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Source_cut_is_captured_once_bound_and_revalidation_uses_current_time()
    {
        await using MemoryEvaluationSourceHarness harness = await MemoryEvaluationSourceHarness.CreateAsync();

        DateTimeOffset cut = DateTimeOffset.UtcNow.AddMinutes(1);

        await harness.Saga.Store.InsertAsync("a", "first", cut.AddMinutes(-2), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.Saga.Store.InsertAsync("b", "second", cut.AddMinutes(-2), null, null, "extraction", harness.Saga.Embedding(), CancellationToken.None);

        await harness.AdmitAsync();

        var a = (await harness.RequestAsync("a")).Request.Memory.Saga[0];

        var b = (await harness.RequestAsync("b")).Request.Memory.Saga[0];

        var request = MemoryEvaluationSourceHarness.Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Memory = new([], [a, b], []) }));

        SourceClock clock = new(cut);

        MemoryEvaluationSourceReader reader = new(new FixedCovenantConnectionSource(harness.Connection), harness.Gate, clock);

        var snapshot = MemoryEvaluationSourceHarness.Require(await reader.ReadAsync(request, harness.Invocation, harness.Lease, CancellationToken.None));

        Assert.Equal(cut, snapshot.AsOfUtc);

        Assert.Equal(1, clock.Reads);

        clock.Current = cut.AddMinutes(1);

        Assert.True((await reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsSuccess);

        Assert.Equal(2, clock.Reads);

        Assert.True((await reader.ValidateAsync(snapshot with { AsOfUtc = cut.AddTicks(1) }, harness.Lease, CancellationToken.None)).IsFailure);

        clock.Current = cut.AddDays(-1);

        Assert.True((await reader.ValidateAsync(snapshot, harness.Lease, CancellationToken.None)).IsFailure);
    }

    private sealed class UnusedConnectionSource : ICovenantConnectionSource
    {
        internal int CoreCalls { get; private set; }

        internal int CanonicalCalls { get; private set; }

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
        {
            CoreCalls++;

            throw new InvalidOperationException("An absent required Covenant port must refuse before core connection access.");
        }

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            CanonicalCalls++;

            throw new InvalidOperationException("An absent required Covenant port must refuse before canonical connection access.");
        }
    }

    private sealed class SourceClock(DateTimeOffset current) : TimeProvider
    {
        internal DateTimeOffset Current { get; set; } = current;

        internal int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;

            return Current;
        }
    }
}
