using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class NativeOrdinalSearchPredicateTests
{
    [Theory]
    [InlineData("before\0café", "CAFÉ")]
    [InlineData("ab\0cd", "ab\0ef")]
    [InlineData("a\0b", "\0B")]
    [InlineData("abc", "\0B")]
    [InlineData("Éric", "éric")]
    [InlineData("café", "cafe")]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public async Task Admitted_predicate_compares_complete_UTF8_arguments(string? value, string? query)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT hex(@value), hex(@query), arcanum_ordinal_contains(@value, @query)";

        command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);

        command.Parameters.AddWithValue("@query", (object?)query ?? DBNull.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(value ?? "")), reader.GetString(0));

        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(query ?? "")), reader.GetString(1));

        bool expected = value is not null && query is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expected, reader.GetBoolean(2));
    }
}
