using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class CampaignRollupStoreTests
{
    internal static readonly Guid Campaign = Guid.Parse("BC037C35-E97D-456B-AE21-42E06BEDD321");

    internal static readonly Guid SessionA = Guid.Parse("CEE41654-6492-4AC7-9CF5-D28983198540");

    [Fact]
    public async Task Successive_folds_retain_linear_source_storage_and_exact_historical_revisions()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        const int sessions = 12;

        List<CampaignRollupArtifact> retained = [];

        for (int index = 0; index < sessions; index++)
        {
            Guid session = index == 0 ? SessionA : Guid.NewGuid();

            if (index > 0)
            {
                await fixture.AddSessionAsync(session, "decision");
            }

            Result<CampaignContributionInput?> prepared = await fixture.Store.PrepareContributionAsync(session, null, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            Result<CampaignRollupArtifact> contribution = await fixture.Store.PublishContributionAsync(prepared.Value!, "decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);

            Result<CampaignRollupInput?> fold = await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None);

            Assert.True(fold.IsSuccess, fold.Error.Message);

            Result<CampaignRollupArtifact> published = await fixture.Store.PublishRollupAsync(fold.Value!, "Campaign decision", null, null, CancellationToken.None);

            Assert.True(published.IsSuccess, published.Error.Message);

            Assert.Equal(index + 1, published.Value.SourceCount);

            retained.Add(published.Value);
        }

        Assert.Equal(sessions, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        foreach (CampaignRollupArtifact artifact in retained)
        {
            Result validated = await fixture.Store.ValidateAsync(artifact, null, CancellationToken.None);

            Assert.True(validated.IsSuccess, validated.Error.Message);
        }

        long storedSources = await fixture.CountAsync("SELECT (SELECT COUNT(*) FROM campaign_rollup_sources) + (SELECT COUNT(*) FROM campaign_rollup_manifest_sources);");

        Assert.True(storedSources <= sessions, $"{sessions} one-Session folds retained {storedSources} source rows; provenance must grow linearly.");
    }

    [Fact]
    public async Task Erasing_an_early_contribution_removes_every_consuming_root_and_shared_physical_source()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignRollupArtifact first = await fixture.PublishCampaignAsync("first summary");

        CampaignRollupArtifact second = await PublishNextSessionAsync(fixture);

        CampaignRollupArtifact third = await PublishNextSessionAsync(fixture);

        using IDisposable authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(fixture.Connection,
            CovenantSqliteAuthorizationKind.SessionRetention);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();

        await using SqliteCommand source = fixture.Connection.CreateCommand();

        source.Transaction = transaction;

        source.CommandText = "SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $session;";

        source.Parameters.AddWithValue("$session", SessionA.ToString("D").ToUpperInvariant());

        Guid contribution = Guid.Parse((string)(await source.ExecuteScalarAsync())!);

        Assert.Equal(10, await CampaignSummaryLifecycle.CountArtifactClosureAsync(fixture.Connection, transaction,
            SensitiveArtifactKind.CampaignContribution, contribution, CancellationToken.None));

        await CampaignSummaryLifecycle.PurgeArtifactAsync(fixture.Connection, transaction,
            SensitiveArtifactKind.CampaignContribution, contribution, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifests;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));

        foreach (CampaignRollupArtifact artifact in new[] { first, second, third })
        {
            Assert.True((await fixture.Store.ValidateAsync(artifact, null, CancellationToken.None)).IsFailure);
        }
    }

    [Fact]
    public async Task Deleting_an_intermediate_summary_preserves_successor_provenance_without_its_content()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignRollupArtifact first = await fixture.PublishCampaignAsync("first summary");

        CampaignRollupArtifact second = await PublishNextSessionAsync(fixture);

        CampaignRollupArtifact third = await PublishNextSessionAsync(fixture);

        using IDisposable authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(fixture.Connection,
            CovenantSqliteAuthorizationKind.SessionRetention);

        await using (SqliteTransaction transaction = fixture.Connection.BeginTransaction())
        {
            await CampaignSummaryLifecycle.PurgeArtifactAsync(fixture.Connection, transaction,
                SensitiveArtifactKind.CampaignRollup, second.ArtifactId, CancellationToken.None);

            await transaction.CommitAsync();
        }

        Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.True((await fixture.Store.ValidateAsync(second, null, CancellationToken.None)).IsFailure);

        Assert.True((await fixture.Store.ValidateAsync(first, null, CancellationToken.None)).IsSuccess);

        Assert.True((await fixture.Store.ValidateAsync(third, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));
    }

    internal static async Task<CampaignRollupArtifact> PublishNextSessionAsync(StoreFixture fixture)
    {
        Guid session = Guid.NewGuid();

        await fixture.AddSessionAsync(session, "next decision");

        Result<CampaignContributionInput?> prepared = await fixture.Store.PrepareContributionAsync(session, null, null, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.True((await fixture.Store.PublishContributionAsync(prepared.Value!, "next decision", null, null, CancellationToken.None)).IsSuccess);

        Result<CampaignRollupInput?> fold = await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None);

        Assert.True(fold.IsSuccess, fold.Error.Message);

        Result<CampaignRollupArtifact> publication = await fixture.Store.PublishRollupAsync(fold.Value!, "next summary", null, null, CancellationToken.None);

        Assert.True(publication.IsSuccess, publication.Error.Message);

        return publication.Value;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_concurrent_source_commit_during_read_validation_refuses_snapshot_promotion_without_partial_publication(bool legacy, bool protectedChange)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        await fixture.ExecuteAsync("PRAGMA journal_mode = WAL;");

        fixture.Connection.DefaultTimeout = 1;

        await fixture.PublishCampaignAsync("first summary");

        Guid next = Guid.NewGuid();

        await fixture.AddSessionAsync(next, "next decision");

        CampaignContributionInput contribution = (await fixture.Store.PrepareContributionAsync(next, null, null, CancellationToken.None)).Value!;

        Assert.True((await fixture.Store.PublishContributionAsync(contribution, "next decision", null, null, CancellationToken.None)).IsSuccess);

        CampaignRollupInput prepared = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        await using SqliteConnection peer = new(fixture.Connection.ConnectionString);

        await peer.OpenAsync();

        peer.DefaultTimeout = 1;

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(peer, CovenantSqliteConnectionMode.ReadWrite, CancellationToken.None);

        bool attempted = false;

        Exception? blockedWriter = null;

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? _, string sql) =>
        {
            if (attempted || !sql.Contains("SELECT SessionId, CampaignId, ContributionArtifactId", StringComparison.Ordinal))
            {
                return;
            }

            attempted = true;

            try
            {
                if (protectedChange)
                {
                    Result<LabeledArtifactWriteReceipt> label = new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(peer))
                        .LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry,
                            Guid.Parse("20601337-085E-4095-9BC0-9D34B70B48E8"), SessionA, Campaign, null, 0,
                            DerivedArtifactContentDigest.ForText("Choose SQLite"), ContentSensitivity.CovenantDerived,
                            GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None).GetAwaiter().GetResult();

                    if (label.IsFailure)
                    {
                        throw new InvalidOperationException(label.Error.Message);
                    }

                    return;
                }

                using SqliteCommand writer = peer.CreateCommand();

                writer.CommandTimeout = 1;

                writer.CommandText = "UPDATE Entries SET Content = 'changed during validation' WHERE SessionId = $session;";

                writer.Parameters.AddWithValue("$session", SessionA.ToString("D").ToUpperInvariant());

                writer.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                blockedWriter = exception;
            }
        }, null);

        Result<CampaignRollupArtifact> publication;

        try
        {
            publication = await fixture.Store.PublishRollupAsync(prepared, "paid result", null, null, CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(attempted);

        Assert.Null(blockedWriter);

        Assert.True(publication.IsFailure);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        if (legacy)
        {
            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));
        }
        else
        {
            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifests;"));

            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));
        }

        Assert.Null((await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_legacy_prefix_is_imported_once_without_rewriting_its_retained_digest_or_summary()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy: true);

        CampaignRollupArtifact legacy = await fixture.PublishCampaignAsync("legacy summary");

        await PublishNextSessionAsync(fixture);

        CampaignRollupArtifact currentLegacy = (await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).Value!;

        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));

        foreach (GrimoireSchemaTransitionStatementResource statement in GrimoireSchemaCatalog.TransitionStatements
            .Where(item => item.TransactionTier == GrimoireSchemaTransactionTier.Core && item.ToVersion == 25))
        {
            await fixture.ExecuteAsync(statement.Sql);
        }

        CampaignRollupArtifact firstShared = await PublishNextSessionAsync(fixture);

        CampaignRollupArtifact secondShared = await PublishNextSessionAsync(fixture);

        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));

        Assert.Equal(4, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));

        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifests;"));

        foreach (CampaignRollupArtifact artifact in new[] { legacy, currentLegacy, firstShared, secondShared })
        {
            Result validated = await fixture.Store.ValidateAsync(artifact, null, CancellationToken.None);

            Assert.True(validated.IsSuccess, validated.Error.Message);
        }

        using IDisposable authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(fixture.Connection,
            CovenantSqliteAuthorizationKind.SessionRetention);

        await using (SqliteTransaction transaction = fixture.Connection.BeginTransaction())
        {
            await CampaignSummaryLifecycle.PurgeArtifactAsync(fixture.Connection, transaction,
                SensitiveArtifactKind.CampaignRollup, currentLegacy.ArtifactId, CancellationToken.None);

            await transaction.CommitAsync();
        }

        Assert.True((await fixture.Store.ValidateAsync(secondShared, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(4, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_manifest_sources;"));
    }

    [Fact]
    public async Task A_shared_root_cannot_acquire_a_second_unverified_legacy_manifest()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignRollupArtifact root = await fixture.PublishCampaignAsync("summary");

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync($"""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT '{root.ArtifactId.ToString("D").ToUpperInvariant()}',SessionId,CampaignId,1,ArtifactId,Revision,
                ContentDigest,SensitivityDigest,SummarizedThroughSequence FROM campaign_contribution_artifacts;
            """));

        Assert.Equal(19, refused.SqliteErrorCode);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));

        Assert.True((await fixture.Store.ValidateAsync(root, null, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task A_short_session_contributes_and_another_session_reads_its_Campaign_decisions()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        Assert.Equal(2, input.Entries.Length);

        Assert.Equal(2, input.SummarizedThroughSequence);

        Result<CampaignRollupArtifact> contribution = await fixture.Store.PublishContributionAsync(input, "Use SQLite for this project.", null, null, CancellationToken.None);

        Assert.True(contribution.IsSuccess, contribution.Error.Message);

        CampaignRollupInput fold = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        Assert.Single(fold.Contributions);

        Result<CampaignRollupArtifact> publication = await fixture.Store.PublishRollupAsync(fold, "Project convention: use SQLite.", null, null, CancellationToken.None);

        Assert.True(publication.IsSuccess, publication.Error.Message);

        CampaignRollupArtifact? warm = (await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).Value;

        Assert.Equal("Project convention: use SQLite.", warm?.Content);

        Assert.Equal(1, warm?.SourceCount);

        Assert.Null((await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Mutating_a_source_before_publication_rejects_output_and_does_not_advance_a_frontier()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        await fixture.ExecuteAsync("UPDATE Entries SET Content = 'Revised decision' WHERE Sequence = 1;");

        Result<CampaignRollupArtifact> stale = await fixture.Store.PublishContributionAsync(input, "old decision", null, null, CancellationToken.None);

        Assert.True(stale.IsFailure);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));
    }

    [Fact]
    public async Task Oversized_output_cannot_advance_contribution_progress()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        Result<CampaignRollupArtifact> tooLarge = await fixture.Store.PublishContributionAsync(input, new string('é', 4097), null, null, CancellationToken.None);

        Assert.True(tooLarge.IsFailure);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.NotNull((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Pure_fork_contributes_nothing_and_only_its_novel_tail_can_promote()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.ExecuteAsync($"INSERT INTO campaign_fork_frontiers(SessionId,SourceSessionId,InheritedThroughSequence,ProofKindCode,CreatedAtUtc) VALUES('{SessionA.ToString().ToUpperInvariant()}','C845CA91-8750-4D49-AED8-7A3EB6455C9E',2,2,'2026-10-01T00:00:00.0000000+00:00');");

        Assert.Null((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).Value);

        await fixture.ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('D0890C41-90F6-42B3-9B18-82B425CCDB76','{SessionA.ToString().ToUpperInvariant()}',1,'Novel choice: use raw SQL','','2026-10-02T00:00:00.0000000+00:00',3);");

        CampaignContributionInput tail = await fixture.PrepareAsync();

        Assert.Single(tail.Entries);

        Assert.Equal("Novel choice: use raw SQL", tail.Entries[0].Content);

        Assert.Equal(2, tail.InheritedThroughSequence);

        Assert.Null(tail.Previous);
    }

    [Fact]
    public async Task Changed_consumed_entries_invalidate_the_Campaign_and_refold_from_current_sources()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        Assert.True((await fixture.Store.PublishContributionAsync(input, "old decision", null, null, CancellationToken.None)).IsSuccess);

        CampaignRollupInput fold = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        CampaignRollupArtifact old = (await fixture.Store.PublishRollupAsync(fold, "old Campaign", null, null, CancellationToken.None)).Value;

        await fixture.ExecuteAsync("UPDATE Entries SET Content = 'new decision' WHERE Sequence = 1;");

        Assert.Null((await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).Value);

        Assert.True((await fixture.Store.ValidateAsync(old, null, CancellationToken.None)).IsFailure);

        CampaignContributionInput changed = await fixture.PrepareAsync();

        Assert.Null(changed.Previous);

        Assert.True((await fixture.Store.PublishContributionAsync(changed, "new decision", null, null, CancellationToken.None)).IsSuccess);

        CampaignRollupInput refold = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        Assert.Null(refold.Previous);

        Assert.Equal("new decision", Assert.Single(refold.Contributions).Content);
    }

    [Fact]
    public async Task Unrelated_updated_time_creates_no_debt_and_new_publication_preserves_a_bound_revision()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        Assert.True((await fixture.Store.PublishContributionAsync(input, "decision", null, null, CancellationToken.None)).IsSuccess);

        CampaignRollupInput fold = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        CampaignRollupArtifact first = (await fixture.Store.PublishRollupAsync(fold, "Campaign decision", null, null, CancellationToken.None)).Value;

        await fixture.ExecuteAsync("UPDATE Sessions SET UpdatedAt = '2026-10-07T00:00:00.0000000+00:00';");

        Assert.Null((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).Value);

        Assert.True((await fixture.Store.ValidateAsync(first, null, CancellationToken.None)).IsSuccess);

        Assert.Empty(await fixture.Store.FindPendingContributionsAsync(DateTimeOffset.UtcNow, 128, CancellationToken.None));
    }

    [Fact]
    public async Task A_new_contributor_that_sorts_before_the_prior_cursor_is_folded_once_and_preserves_a_bound_revision()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignRollupArtifact first = await fixture.PublishCampaignAsync("first Campaign");

        Guid earlier = Guid.Parse("11111111-1111-4111-8111-111111111111");

        await fixture.AddSessionAsync(earlier, "earlier-sorting novel decision");

        CampaignContributionInput native = (await fixture.Store.PrepareContributionAsync(earlier, null, null, CancellationToken.None)).Value!;

        Assert.True((await fixture.Store.PublishContributionAsync(native, "second decision", null, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(first.ArtifactId, (await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).Value!.ArtifactId);

        CampaignRollupInput page = (await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

        Assert.Equal(earlier, Assert.Single(page.Contributions).SessionId);

        Assert.Equal(first.ArtifactId, page.Previous!.ArtifactId);

        Result<CampaignRollupArtifact> second = await fixture.Store.PublishRollupAsync(page, "both decisions", null, null, CancellationToken.None);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(2, second.Value.SourceCount);

        Assert.True((await fixture.Store.ValidateAsync(first, null, CancellationToken.None)).IsSuccess);

        Assert.Null((await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Two_large_entries_are_paid_by_bounded_pages_without_double_folding_the_first()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.ExecuteAsync("UPDATE Entries SET Content = replace(hex(zeroblob(10000)), '0', 'x');");

        CampaignContributionInput first = await fixture.PrepareAsync();

        Assert.Single(first.Entries);

        Assert.True(first.HasMore);

        Assert.Equal(1, first.SummarizedThroughSequence);

        Assert.True((await fixture.Store.PublishContributionAsync(first, "paid first", null, null, CancellationToken.None)).IsSuccess);

        CampaignContributionInput second = await fixture.PrepareAsync();

        Assert.Single(second.Entries);

        Assert.Equal(2, second.Entries[0].Sequence);

        Assert.Equal("paid first", second.Previous!.Content);

        Assert.True((await fixture.Store.PublishContributionAsync(second, "paid both", null, null, CancellationToken.None)).IsSuccess);

        Assert.Null((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).Value);

        Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
    }

    [Theory]
    [InlineData(false, 32768)]
    [InlineData(false, 32769)]
    [InlineData(true, 24576)]
    [InlineData(true, 24577)]
    public async Task Oversized_native_entries_use_bounded_omission_and_only_successful_publication_pays_progress(bool previous, int bytes)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        if (previous)
        {
            CampaignContributionInput initial = (await fixture.Store.PrepareContributionAsync(SessionA, 1, null, CancellationToken.None)).Value!;

            Assert.True((await fixture.Store.PublishContributionAsync(initial, string.Join(" ", Enumerable.Repeat("a", 4096)) + ".", null, null, CancellationToken.None)).IsSuccess);
        }

        long sequence = previous ? 2 : 1;

        await fixture.ExecuteAsync($"UPDATE Entries SET Content = '{new string('z', bytes)}' WHERE Sequence = {sequence};");

        CampaignContributionInput input = await fixture.PrepareAsync();

        string content = input.Entries[0].Content;

        Assert.Equal(bytes > (previous ? 24576 : 32768) ? $"[omitted: {bytes} bytes]" : new string('z', bytes), content);

        Assert.Equal(previous ? 1 : 0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));

        Assert.True((await fixture.Store.PublishContributionAsync(input, "bounded output", null, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(input.SummarizedThroughSequence, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));

        Result<CampaignContributionInput?> next = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        if (next.Value is { } tail)
        {
            Assert.True(tail.Entries[0].Sequence > input.SummarizedThroughSequence);

            Assert.True((await fixture.Store.PublishContributionAsync(tail, "later output", null, null, CancellationToken.None)).IsSuccess);
        }

        Assert.Null((await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None)).Value);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("marker")]
    [InlineData("digest")]
    [InlineData("delete")]
    public async Task Omitted_source_substitution_cannot_pay_a_cursor(string substitution)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        string source = string.Concat(Enumerable.Repeat("😀", 8193));

        await fixture.ExecuteAsync($"UPDATE Entries SET Content = '{source}' WHERE Sequence = 1;");

        CampaignContributionInput input = await fixture.PrepareAsync();

        Assert.Equal("[omitted: 32772 bytes]", input.Entries[0].Content);

        Assert.Equal(DerivedArtifactContentDigest.ForText(source), input.Entries[0].OriginalContentDigest);

        if (substitution == "body")
        {
            await fixture.ExecuteAsync($"UPDATE Entries SET Content = '{string.Concat(Enumerable.Repeat("😁", 8193))}' WHERE Sequence = 1;");
        }
        else if (substitution == "delete")
        {
            await fixture.ExecuteAsync("DELETE FROM Entries WHERE Sequence = 1;");
        }
        else
        {
            CampaignContributionEntry entry = input.Entries[0];

            entry = substitution == "marker"
                ? entry with { Content = "substitute", ContentDigest = DerivedArtifactContentDigest.ForText("substitute") }
                : entry with { OriginalContentDigest = DerivedArtifactContentDigest.ForText("substitute") };

            input = input with { Entries = input.Entries.SetItem(0, entry) };
        }

        Assert.True((await fixture.Store.PublishContributionAsync(input, "unpaid", null, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
    }

    [Fact]
    public async Task Oversized_protected_sources_preserve_original_label_authority_and_provenance()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        string source = new('z', 34000);

        await fixture.ExecuteAsync($"UPDATE Entries SET Content = '{source}' WHERE Sequence = 1;");

        Guid generation = await fixture.MarkEntryProtectedAsync(source);

        Result<CampaignContributionInput?> denied = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        Assert.True(denied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, denied.Error.Code);

        await using TestReadLease authority = new(Campaign);

        CampaignContributionInput input = (await fixture.Store.PrepareContributionAsync(SessionA, null, authority, CancellationToken.None)).Value!;

        Assert.Equal("[omitted: 34000 bytes]", input.Entries[0].Content);

        Assert.Equal(DerivedArtifactContentDigest.ForText(source), input.Entries[0].OriginalContentDigest);

        Assert.Equal(ContentSensitivity.CovenantDerived, input.Sensitivity);

        Assert.Contains(generation, input.Provenance.ExactGenerationIds);

        await fixture.ExecuteAsync($"UPDATE Entries SET Content = '{new string('y', 34000)}' WHERE Sequence = 1;");

        Result<CampaignContributionInput?> staleLabel = await fixture.Store.PrepareContributionAsync(SessionA, null, authority, CancellationToken.None);

        Assert.True(staleLabel.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, staleLabel.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));
    }

    [Fact]
    public async Task Pending_keyset_can_reach_a_healthy_session_beyond_a_full_pending_page()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        for (int index = 1; index <= 129; index++)
        {
            await fixture.AddSessionAsync(Guid.Parse($"{index:x8}-0000-4000-8000-000000000000"), "pending");
        }

        IReadOnlyList<Guid> first = await fixture.Store.FindPendingContributionsForCampaignAsync(Campaign, DateTimeOffset.MaxValue, 128, CancellationToken.None);

        IReadOnlyList<Guid> second = await fixture.Store.FindPendingContributionsForCampaignAsync(Campaign, DateTimeOffset.MaxValue, 128, CancellationToken.None, first[^1]);

        Assert.Equal(128, first.Count);

        Assert.Equal(2, second.Count);

        Assert.Empty(first.Intersect(second));

        Assert.Contains(Guid.Parse("00000081-0000-4000-8000-000000000000"), second);
    }

    [Fact]
    public async Task A_substituted_prepared_entry_body_cannot_authorize_publication()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        CampaignContributionInput forged = input with { Entries = input.Entries.SetItem(0, input.Entries[0] with { Content = "forged instruction" }) };

        Assert.True((await fixture.Store.PublishContributionAsync(forged, "forged output", null, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));
    }

    [Fact]
    public async Task Pending_assistants_and_tool_or_system_payloads_do_not_enter_a_contribution_page()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.ExecuteAsync($"""
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            VALUES('A0000000-0000-4000-8000-000000000003','{SessionA.ToString().ToUpperInvariant()}',2,'pending placeholder','','2026-10-02T00:00:00.0000000Z',3),
                  ('A0000000-0000-4000-8000-000000000004','{SessionA.ToString().ToUpperInvariant()}',3,hex(zeroblob(20000)),'','2026-10-02T00:00:00.0000000Z',4),
                  ('A0000000-0000-4000-8000-000000000005','{SessionA.ToString().ToUpperInvariant()}',0,hex(zeroblob(20000)),'','2026-10-02T00:00:00.0000000Z',5);
            """);

        CampaignContributionInput input = await fixture.PrepareAsync();

        Assert.Equal(2, input.Entries.Length);

        Assert.All(input.Entries, entry => Assert.Equal(1, entry.Role));

        Assert.Equal(2, input.SummarizedThroughSequence);

        Assert.False(input.HasMore);
    }

    [Fact]
    public async Task A_protected_entry_requires_Campaign_coverage_and_an_acknowledged_publication()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        Guid generation = await fixture.MarkEntryProtectedAsync();

        Result<CampaignContributionInput?> cleanOnly = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        Assert.True(cleanOnly.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, cleanOnly.Error.Code);

        await using TestReadLease wrong = new(Guid.NewGuid());

        Assert.True((await fixture.Store.PrepareContributionAsync(SessionA, null, wrong, CancellationToken.None)).IsFailure);

        await using TestReadLease authority = new(Campaign);

        CampaignContributionInput input = (await fixture.Store.PrepareContributionAsync(SessionA, null, authority, CancellationToken.None)).Value!;

        Assert.NotNull(input);

        Assert.Equal(ContentSensitivity.CovenantDerived, input.Sensitivity);

        Assert.Contains(generation, input.Provenance.ExactGenerationIds);

        Assert.True(authority.Validations >= 2);

        Assert.True((await fixture.Store.PublishContributionAsync(input, "protected output", null, authority, CancellationToken.None)).IsFailure);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
    }

    [Fact]
    public async Task Cancellation_before_publication_leaves_all_artifacts_and_cursors_unpaid()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PublishContributionAsync(input,
            "cancelled output", null, null, cancelled.Token));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("decision /Users/operator/secret.txt")]
    [InlineData("decision file:///private/attachment")]
    [InlineData("data:application/octet-stream;base64,AAAA")]
    public async Task Direct_publication_enforces_the_shared_summary_policy_before_paying_progress(string output)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        CampaignContributionInput input = await fixture.PrepareAsync();

        Result<CampaignRollupArtifact> refused = await fixture.Store.PublishContributionAsync(input, output, null, null, CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, refused.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));
    }

    [Fact]
    public async Task A_Campaign_pending_page_contains_only_unpaid_canonically_bound_contributors()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        Guid peer = Guid.NewGuid();

        await fixture.AddSessionAsync(peer, "peer decision");

        Guid outside = Guid.NewGuid();

        Guid otherCampaign = Guid.NewGuid();

        await fixture.ExecuteAsync($"""
            INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt) VALUES('{otherCampaign.ToString().ToUpperInvariant()}','outside','outside','/outside',0,char(123)||char(125),'2026-10-01T00:00:00.0000000Z','2026-10-01T00:00:00.0000000Z');
            INSERT INTO Sessions(Id,CampaignId,Title,CreatedAt,UpdatedAt) VALUES('{outside.ToString().ToUpperInvariant()}','{otherCampaign.ToString().ToUpperInvariant()}','outside','2026-10-01T00:00:00.0000000Z','2026-10-01T00:00:00.0000000Z');
            INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES('{outside.ToString().ToUpperInvariant()}',2,'{otherCampaign.ToString().ToUpperInvariant()}','2026-10-01T00:00:00.0000000Z');
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{Guid.NewGuid().ToString().ToUpperInvariant()}','{outside.ToString().ToUpperInvariant()}',1,'outside decision','','2026-10-01T00:00:00.0000000Z',1);
            """);

        IReadOnlyList<Guid> pending = await fixture.Store.FindPendingContributionsForCampaignAsync(Campaign, DateTimeOffset.MaxValue, 128, CancellationToken.None);

        Assert.Equal(2, pending.Count);

        Assert.Contains(SessionA, pending);

        Assert.Contains(peer, pending);

        Assert.DoesNotContain(outside, pending);

        await fixture.Store.PublishContributionAsync(await fixture.PrepareAsync(), "paid", null, null, CancellationToken.None);

        IReadOnlyList<Guid> remaining = await fixture.Store.FindPendingContributionsForCampaignAsync(Campaign, DateTimeOffset.MaxValue, 128, CancellationToken.None);

        Assert.Equal(peer, Assert.Single(remaining));
    }

    [Theory]
    [InlineData("lower(ArtifactId)")]
    [InlineData("lower(replace(ArtifactId,'-',''))")]
    public async Task A_foreign_label_spelling_cannot_be_treated_as_a_clean_native_Entry(string spelling)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.MarkEntryProtectedAsync();

        // ArtifactId is outside the governed identity family. Simulate a valid historical
        // spelling without changing the GUID bound by the immutable label digest.
        await fixture.ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET ArtifactId = " + spelling + ";");

        Result<CampaignContributionInput?> prepared = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        Assert.True(prepared.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, prepared.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
    }

    internal sealed class StoreFixture(CovenantSchemaScratchDatabase database) : IAsyncDisposable
    {
        internal SqliteConnection Connection => database.Connection;

        internal Guid CampaignId => Campaign;

        internal Guid SessionId => SessionA;

        internal CampaignRollupStore Store { get; } = new(new FixedCovenantConnectionSource(database.Connection), CovenantSqliteConnectionInitializer.Instance);

        internal static async Task<StoreFixture> CreateAsync(bool legacy = false)
        {
            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

            await database.InstallCoreObjectsAsync([
                "Campaigns", "Sessions", "Entries", "session_campaign_bindings", "artifact_sensitivity", "session_sensitivity_state", "assistant_entry_finalizations", "session_turn_claims",
                .. CovenantSchemaScratchDatabase.ArtifactSensitivityDeleteGuardObjects, "artifact_sensitivity_guard_update",
            ], CancellationToken.None);

            foreach (GrimoireSchemaObject item in (legacy ? CoreSchemaVersionTwentyFourFixture.Objects : GrimoireSchemaCatalog.CoreObjects).Where(item => item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
            {
                await database.ExecuteAsync(item.Sql, CancellationToken.None);
            }

            foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(item => item.Name.Contains("campaign_contribution", StringComparison.Ordinal) && !item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
            {
                await database.InstallCoreObjectsAsync([item.Name], CancellationToken.None);
            }

            await database.ExecuteAsync($"""
                INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt) VALUES('{Campaign.ToString().ToUpperInvariant()}','project','project','/test-campaign',0,char(123) || char(125),'2026-10-01T00:00:00.0000000+00:00','2026-10-01T00:00:00.0000000+00:00');
                INSERT INTO Sessions(Id,CampaignId,Title,CreatedAt,UpdatedAt) VALUES('{SessionA.ToString().ToUpperInvariant()}','{Campaign.ToString().ToUpperInvariant()}','first','2026-10-01T00:00:00.0000000+00:00','2026-10-01T00:00:00.0000000+00:00');
                INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES('{SessionA.ToString().ToUpperInvariant()}',2,'{Campaign.ToString().ToUpperInvariant()}','2026-10-01T00:00:00.0000000+00:00');
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('20601337-085E-4095-9BC0-9D34B70B48E8','{SessionA.ToString().ToUpperInvariant()}',1,'Choose SQLite','','2026-10-01T00:00:00.0000000+00:00',1);
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('45AE28B6-1CDC-4127-91F4-04DAC8C4446A','{SessionA.ToString().ToUpperInvariant()}',1,'Agreed: SQLite','','2026-10-01T00:00:01.0000000+00:00',2);
                """, CancellationToken.None);

            return new StoreFixture(database);
        }

        internal async Task<CampaignContributionInput> PrepareAsync()
        {
            Result<CampaignContributionInput?> prepared = await Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            Assert.NotNull(prepared.Value);

            return prepared.Value;
        }

        internal Task ExecuteAsync(string sql) => database.ExecuteAsync(sql, CancellationToken.None);

        internal Task<long> CountAsync(string sql) => database.ScalarLongAsync(sql, CancellationToken.None);

        internal async Task<CampaignRollupArtifact> PublishCampaignAsync(string content)
        {
            CampaignContributionInput input = await PrepareAsync();

            Result<CampaignRollupArtifact> contribution = await Store.PublishContributionAsync(input, "decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);

            CampaignRollupInput fold = (await Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value!;

            Result<CampaignRollupArtifact> publication = await Store.PublishRollupAsync(fold, content, null, null, CancellationToken.None);

            Assert.True(publication.IsSuccess, publication.Error.Message);

            return publication.Value;
        }

        internal Task AddSessionAsync(Guid sessionId, string content) => ExecuteAsync($"""
            INSERT INTO Sessions(Id,CampaignId,Title,CreatedAt,UpdatedAt) VALUES('{sessionId.ToString().ToUpperInvariant()}','{Campaign.ToString().ToUpperInvariant()}','peer','2026-10-01T00:00:00.0000000Z','2026-10-01T00:00:00.0000000Z');
            INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES('{sessionId.ToString().ToUpperInvariant()}',2,'{Campaign.ToString().ToUpperInvariant()}','2026-10-01T00:00:00.0000000Z');
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{Guid.NewGuid().ToString().ToUpperInvariant()}','{sessionId.ToString().ToUpperInvariant()}',1,'{content.Replace("'", "''", StringComparison.Ordinal)}','','2026-10-01T00:00:00.0000000Z',1);
            """);

        internal async Task<Guid> MarkEntryProtectedAsync(string content = "Choose SQLite")
        {
            Guid generation = Guid.NewGuid();

            Result<LabeledArtifactWriteReceipt> result = await new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(Connection))
                .LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry,
                    Guid.Parse("20601337-085E-4095-9BC0-9D34B70B48E8"), SessionA, Campaign, null, 0,
                    DerivedArtifactContentDigest.ForText(content), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([generation])), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            return generation;
        }

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class TestReadLease(Guid campaignId) : ICovenantSnapshotReadLease
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, CovenantLeaseKind.Read,
            CovenantLeaseCoverage.Scoped, CovenantOperationScope.ForCampaign(campaignId), Guid.NewGuid(), 1, 1, 0, 1,
            null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        internal int Validations { get; private set; }

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            Validations++;

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
