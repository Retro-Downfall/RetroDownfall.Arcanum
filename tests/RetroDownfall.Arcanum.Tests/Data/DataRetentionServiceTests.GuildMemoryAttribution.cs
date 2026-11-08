using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_reset_explicitly_deletes_and_reconciles_Guild_attribution_without_foreign_keys(bool retainAttribution)
    {
        RequireSqlCipher();

        await using GuildSignetAuthorityTests.Fixture authority = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await authority.OpenAsync();

        await using ArcanumDbContext resetContext = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(resetContext, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Annals = false } }), MemoryErasureTestKeys.Isolated());

        GuildGrant grant = authority.Grant with
        {
            Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Global))],
        };

        authority.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await authority.RootAsync(grant);

        using (GuildMemoryInvocationAmbient.Begin(new(authority.Authority, execution)))
        {
            var written = await lexicon.UpsertAsync("Factory attribution", null, ["guild fact"], LexiconScope.Global);

            Assert.True(written.IsSuccess, written.Error.Message);
        }

        Assert.Equal(1L, await authority.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));

        ArcanumDbContext originalContext = _db!;

        _db = resetContext;

        try
        {
            await ExecuteAsync("PRAGMA foreign_keys=OFF");

            Assert.Equal(0, await CampaignScalarCountAsync("PRAGMA foreign_keys"));

            if (retainAttribution)
            {
                await ExecuteAsync("""
                    CREATE TEMP TRIGGER retain_guild_attribution AFTER DELETE ON guild_memory_attribution
                    BEGIN
                        INSERT INTO guild_memory_attribution(StoreCode,SubjectId,ImmutableVersionId,SignetId,AgentId,RunId,QuestId)
                        VALUES(OLD.StoreCode,OLD.SubjectId,OLD.ImmutableVersionId,OLD.SignetId,OLD.AgentId,OLD.RunId,OLD.QuestId);
                    END;
                    """);
            }

            var recovered = await ReconcileFactoryResetV0Async(CreateService(), "guild-attribution-reset");

            Assert.Equal(retainAttribution ? 1 : 0, recovered.Summary.RequiresAttention);

            Assert.Equal(retainAttribution ? 0 : 1, recovered.Summary.Completed);

            Assert.Equal(retainAttribution ? 1L : 0L, await authority.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
        }
        finally
        {
            _db = originalContext;
        }
    }
}
