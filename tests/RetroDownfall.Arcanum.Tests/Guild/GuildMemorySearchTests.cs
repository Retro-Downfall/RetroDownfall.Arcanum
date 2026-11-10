using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMemorySearchTests
{
    [Fact]
    public async Task Native_workspace_search_never_treats_a_memory_admission_as_physical_source_authority()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        await using var connection = await f.OpenAsync();

        await using var insert = connection.CreateCommand();

        insert.CommandText = """
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES('workspace-secret','/fake/workspace','secret.txt',0,'privateworkspaceneedle',0,22,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')
            """;

        await insert.ExecuteNonQueryAsync();

        var result = await NativeMemorySearchReader.ReadAsync(connection, "privateworkspaceneedle",
            MemorySearchScope.Workspace, null, null, new(3), 1, default);

        Assert.True(result.IsFailure);

        Assert.Equal("Guild.AuthorityUnavailable", result.Error.Code);
    }
    [Fact]
    public async Task Lexicon_search_filters_unauthorized_shadowing_tier_before_limit()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, Guid.NewGuid(), Guid.NewGuid());

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new()), MemoryErasureTestKeys.Isolated());

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["allowed global"], LexiconScope.Global)).IsSuccess);

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["denied Campaign"], LexiconScope.ForCampaign(campaign))).IsSuccess);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var page = await lexicon.SearchMemoryPageAsync("needle", LexiconScope.ForCampaign(campaign), 1, null);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal("allowed global", Assert.Single(Assert.Single(page.Value).Entry.Facts));
    }

    [Fact]
    public async Task Saga_search_applies_explicit_Global_grant_before_the_newest_first_limit()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        SagaMemoryStore saga = new(db, new TestOptionsMonitor<ArcanumSettings>(new()), MemoryErasureTestKeys.Isolated());

        float[] embedding = new float[768];

        embedding[0] = 1;

        await saga.InsertAsync("allowed-global", "needle allowed global", f.Time.GetUtcNow(), null, null, null, embedding, default);

        await saga.InsertAsync("denied-campaign", "needle denied Campaign", f.Time.GetUtcNow().AddSeconds(1), session, null, null, embedding, default);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var page = await saga.SearchMemoryPageAsync("needle", null, MemoryScope.Installation, 1, null, default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal("allowed-global", Assert.Single(page.Value).Memory.Id);
    }
    [Fact]
    public async Task Native_transcript_scope_filters_denied_newer_rows_before_the_window_limit()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        Guid denied = Guid.NewGuid();

        await using var connection = await f.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES($denied,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            INSERT INTO session_campaign_bindings VALUES($session,2,$campaign,'2026-01-01T00:00:00.0000000Z'),($denied,1,NULL,'2026-01-01T00:00:00.0000000Z');
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            VALUES('11111111-1111-4111-8111-111111111111',$session,0,'needle allowed','test','2026-01-01T00:00:00.0000000Z',1),
                ('22222222-2222-4222-8222-222222222222',$denied,0,'needle denied','test','2026-01-02T00:00:00.0000000Z',1);
            """;

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$campaign", campaign.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$denied", denied.ToString("D").ToUpperInvariant());

        using (CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
        {
            await command.ExecuteNonQueryAsync();
        }

        f.Policy.NextOwners = new(session, campaign, null);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read,
            new(GuildMemoryScopeKind.Campaign, campaign))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var page = await GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Transcript, GuildMemoryOperation.Read,
            ct => NativeMemorySearchReader.ReadAsync(connection, "needle", MemorySearchScope.Session, null, null, new(0), 1, ct), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal("11111111-1111-4111-8111-111111111111", Assert.Single(page.Value.Hits).Result.SourceId);
    }
    [Fact]
    public async Task Lexicon_continuation_cannot_use_an_unauthorized_anchor_to_influence_allowed_ordering()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, Guid.NewGuid(), Guid.NewGuid());

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new()), MemoryErasureTestKeys.Isolated());

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["allowed global"], LexiconScope.Global)).IsSuccess);

        var denied = await lexicon.UpsertAsync("needle", "fact", ["denied Campaign"], LexiconScope.ForCampaign(campaign));

        Assert.True(denied.IsSuccess);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var page = await lexicon.SearchMemoryPageAsync("needle", LexiconScope.ForCampaign(campaign), 1,
            new("", campaign.ToString("D"), denied.Value.Id.ToString("N")));

        Assert.True(page.IsFailure);

        Assert.Equal("Validation.InvalidBody", page.Error.Code);
    }
}
