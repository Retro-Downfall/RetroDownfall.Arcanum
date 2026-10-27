using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests
{
    [Fact]
    public void Ordinal_ASCII_case_classes_have_no_non_ASCII_scalar_members()
    {
        HashSet<string> ascii = new(Enumerable.Range('A', 26).Select(value => ((char)value).ToString()), StringComparer.OrdinalIgnoreCase);

        List<int> aliases = [];

        int examined = 0;

        for (int value = 128; value <= 0x10ffff; value++)
        {
            if (!Rune.IsValid(value)) continue;

            examined++;

            if (ascii.Contains(new Rune(value).ToString())) aliases.Add(value);
        }

        Assert.Equal(1_111_936, examined);

        Assert.Empty(aliases);
    }

    [Theory]
    [InlineData("ÉabcÉ", "éABCé")]
    [InlineData("éabcé", "ÉABCÉ")]
    [InlineData("ÉabcÉ", "prefix\0éABCé")]
    public async Task Mixed_Unicode_query_uses_a_complete_safe_seed_to_find_a_hit_beyond_the_scan_window(string query, string content)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await SeedUnicodeWindowAsync(connection, content);

        NativeMemorySearchPage page = await ReadNativeAsync(connection, query, MemorySearchScope.Workspace, null, null, new(3), 20, CancellationToken.None);

        Assert.Equal("0000000000001200", Assert.Single(page.Hits).Result.SourceId);

        Assert.Equal(content, page.Hits[0].Result.Content);

        Assert.Null(page.Reason);
    }

    [Fact]
    public async Task Pure_Unicode_query_preserves_bounded_progress_until_the_last_match()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await SeedUnicodeWindowAsync(connection, "ςσΣ");

        MemorySearchPosition position = new(3);

        for (int number = 0; number < 3; number++)
        {
            long steps = 0;

            raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);

            NativeMemorySearchPage page;

            try
            {
                page = await ReadNativeAsync(connection, "ΣΣΣ", MemorySearchScope.Workspace, null, null, position, 20, CancellationToken.None);
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            }

            Assert.InRange(steps, 1, 40000);

            if (number < 2)
            {
                Assert.Empty(page.Hits);

                Assert.Equal(MemorySearchTruncationReason.ExaminedRows, page.Reason);

                Assert.NotEqual(position, page.Position);

                position = page.Position;
            }
            else
            {
                Assert.Equal("0000000000001200", Assert.Single(page.Hits).Result.SourceId);

                Assert.Null(page.Reason);
            }
        }
    }

    private static async Task SeedUnicodeWindowAsync(SqliteConnection connection, string content)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<1200)
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('%016d',v),'/fake/workspace','same.cs',v,CASE WHEN v=1200 THEN @content ELSE 'ordinary' END,
                0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """;

        command.Parameters.AddWithValue("@content", content);

        _ = await command.ExecuteNonQueryAsync();
    }
}
