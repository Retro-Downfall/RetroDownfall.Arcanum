using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("question")]
    [InlineData("continuation")]
    [InlineData("result")]
    [InlineData("child")]
    public async Task Tree_refuses_non_guid_identity_text_without_throwing_or_inventing_a_state(string identity)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = await ReceiptRootAsync(f);

        var child = await f.ChildAsync(root, await f.DriverAsync(root));

        const string malformed = "ZZZZZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZZZZZZZZZ";

        string childId = child.Quest.Id.ToString("D").ToUpperInvariant();

        if (identity is "question" or "continuation")
        {
            await f.ExecuteAsync($"""
                PRAGMA ignore_check_constraints=ON;
                UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,
                    QuestionId='{(identity == "question" ? malformed : Guid.NewGuid().ToString("D"))}',
                    ContinuationId='{(identity == "continuation" ? malformed : Guid.NewGuid().ToString("D"))}',Revision=Revision+1
                    WHERE Id='{childId}';
                UPDATE LongRunningOperations SET State=2 WHERE Id='{child.Quest.OperationId:N}';
                PRAGMA ignore_check_constraints=OFF;
                """);
        }
        else if (identity == "result")
        {
            await f.ExecuteAsync($"""
                PRAGMA ignore_check_constraints=ON;
                INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                VALUES('{childId}','{malformed}',zeroblob(32),4,'Guild.Unavailable',1,1,
                    '{Guid.NewGuid().ToString("D").ToUpperInvariant()}',2,'2026-11-08T00:00:00.0000000Z');
                UPDATE LongRunningOperations SET State=5,TerminalErrorCode='Guild.Unavailable' WHERE Id='{child.Quest.OperationId:N}';
                PRAGMA ignore_check_constraints=OFF;
                """);
        }
        else
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT sql FROM sqlite_master WHERE name='guild_quests_guard_update'";

            string questTrigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

            command.CommandText = "SELECT sql FROM sqlite_master WHERE name='guild_signets_guard_update'";

            string signetTrigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

            command.CommandText = $"""
                PRAGMA foreign_keys=OFF;
                PRAGMA ignore_check_constraints=ON;
                DROP TRIGGER guild_quests_guard_update;
                DROP TRIGGER guild_signets_guard_update;
                UPDATE guild_quests SET Id='{malformed}',Revision=Revision+1 WHERE Id='{childId}';
                UPDATE guild_signets SET QuestId='{malformed}',Revision=Revision+1 WHERE QuestId='{childId}';
                UPDATE LongRunningOperations SET CheckpointReference='{malformed}',
                    CheckpointPayload=CAST(replace(CAST(CheckpointPayload AS TEXT),lower('{childId}'),lower('{malformed}')) AS BLOB)
                    WHERE Id='{child.Quest.OperationId:N}';
                {questTrigger};
                {signetTrigger};
                PRAGMA ignore_check_constraints=OFF;
                PRAGMA foreign_keys=ON;
                """;

            _ = await command.ExecuteNonQueryAsync();
        }

        var page = await TreeReadAsync(f, root.Quest.Id, new());

        Assert.Equal(ErrorCodes.Guild.Unavailable, page.Error.Code);
    }
}
