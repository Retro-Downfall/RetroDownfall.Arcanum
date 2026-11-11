using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildCohortContentClockTests
{
    [Fact]
    public async Task Deleting_Guild_content_advances_the_durable_candidate_visibility_epoch()
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',0,'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,zeroblob(32),'text/plain',X'78',0,0,'2026-11-10T00:00:00.0000000Z');
            DELETE FROM guild_content WHERE ContentId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA';
            SELECT Revision FROM guild_cohort_content_clock WHERE Id=1;
            """;

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
