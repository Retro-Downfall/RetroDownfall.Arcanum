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

    [Fact]
    public async Task An_oversized_entry_fails_before_materialization_and_does_not_pay_a_cursor()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        await fixture.ExecuteAsync("UPDATE Entries SET Content = hex(zeroblob(17000)) WHERE Sequence = 1;");

        Result<CampaignContributionInput?> result = await fixture.Store.PrepareContributionAsync(SessionA, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("campaign_rollup.input_too_large", result.Error.Code);

        Assert.Equal(0, await fixture.CountAsync("SELECT SummarizedThroughSequence FROM campaign_contribution_state;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));
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

        internal static async Task<StoreFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

            await database.InstallCoreObjectsAsync([
                "Campaigns", "Sessions", "Entries", "session_campaign_bindings", "artifact_sensitivity", "session_sensitivity_state", "assistant_entry_finalizations", "session_turn_claims",
                "artifact_sensitivity_guard_delete", "artifact_sensitivity_guard_update",
            ], CancellationToken.None);

            foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(item => item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
            {

                await database.InstallCoreObjectsAsync([item.Name], CancellationToken.None);

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

        internal async Task<Guid> MarkEntryProtectedAsync()
        {
            Guid generation = Guid.NewGuid();

            Result<LabeledArtifactWriteReceipt> result = await new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(Connection))
                .LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry,
                    Guid.Parse("20601337-085E-4095-9BC0-9D34B70B48E8"), SessionA, Campaign, null, 0,
                    DerivedArtifactContentDigest.ForText("Choose SQLite"), ContentSensitivity.CovenantDerived,
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
