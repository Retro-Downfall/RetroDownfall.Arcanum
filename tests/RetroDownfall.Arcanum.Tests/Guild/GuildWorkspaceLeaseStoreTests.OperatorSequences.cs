using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_sequence_integrity_rejects_missing_membership_and_rewound_watermark(bool rewind)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = Row();

        Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

        Assert.True((await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "released", default)).IsSuccess);

        await GuildStorageIntegrity.ValidateAsync(connection, transaction, default, 14);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        if (rewind)
        {
            command.CommandText = "UPDATE sqlite_sequence SET seq=0 WHERE name='guild_operator_sequences'";
        }
        else
        {
            command.CommandText = "DROP TRIGGER guild_operator_sequences_guard_delete; DELETE FROM guild_operator_sequences";
        }

        _ = await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(connection, transaction, default, 14));
    }
}
