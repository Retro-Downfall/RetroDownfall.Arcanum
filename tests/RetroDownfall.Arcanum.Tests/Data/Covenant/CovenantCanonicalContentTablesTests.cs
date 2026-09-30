using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one list of canonical content tables, checked against the catalog and the foreign keys.
/// </summary>
/// <remarks>
/// Family reset, its emptiness proof, the restore inspector and purger, and the reset inventory all
/// read <see cref="CovenantCanonicalContentTables.InDeletionOrder"/>. The expected set is derived from
/// the canonical catalog rather than restated, so a canonical table added later fails here until the
/// list names it, and the order is checked against the foreign keys SQLite reports rather than against
/// a second hand-written order.
/// </remarks>
public sealed class CovenantCanonicalContentTablesTests
{

    private static CancellationToken Token => CancellationToken.None;

    private static IReadOnlyList<string> Tables => CovenantCanonicalContentTables.InDeletionOrder;

    [Fact]
    public void The_list_names_every_canonical_table_except_the_state_singleton()
    {

        string[] expected =
        [
            .. GrimoireSchemaCatalog.CovenantCanonicalObjects
                .Where(static definition => definition.Category == GrimoireSchemaCategory.Tables
                    && !string.Equals(definition.Name, "covenant_state", StringComparison.Ordinal))
                .Select(static definition => definition.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expected, Tables.Order(StringComparer.Ordinal));

        Assert.Equal(15, Tables.Count);

        Assert.Equal(Tables.Count, Tables.Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public async Task Every_table_is_deleted_before_each_table_it_references()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await database.InstallCanonicalAsync(Token);

        int references = 0;

        foreach (string table in Tables)
        {

            foreach (string referenced in await ReferencedTablesAsync(database.Connection, table))
            {

                if (string.Equals(referenced, table, StringComparison.Ordinal) || !Tables.Contains(referenced))
                {

                    continue;

                }

                references++;

                Assert.True(
                    IndexOf(table) < IndexOf(referenced),
                    $"{table} references {referenced}");

            }

        }

        // The canonical tier does declare foreign keys between its content tables. A loop that found
        // none would pass for any order at all.
        Assert.True(references >= 5, $"Only {references} canonical foreign keys were found.");

    }

    [Fact]
    public void Key_epochs_are_deleted_last_because_deleting_heads_rewrites_them()
    {

        Assert.Equal("covenant_key_epochs", Tables[^1]);

        Assert.True(IndexOf("covenant_heads") < IndexOf("covenant_key_epochs"));

    }

    [Fact]
    public void Every_consumer_reads_the_one_list()
    {

        Assert.Same(Tables, CovenantCanonicalErasureTransaction.FamilyTables);

        Assert.Same(Tables, BackupRestoreProtectedStateInspector.CanonicalContentTables);

    }

    private static int IndexOf(string table)
    {

        for (int index = 0; index < Tables.Count; index++)
        {

            if (string.Equals(Tables[index], table, StringComparison.Ordinal))
            {

                return index;

            }

        }

        return -1;

    }

    private static async Task<List<string>> ReferencedTablesAsync(SqliteConnection connection, string table)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";

        List<string> referenced = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {

            // Column 2 is the referenced table's name.
            referenced.Add(reader.GetString(2));

        }

        return referenced;

    }

}
