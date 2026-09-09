using Microsoft.Data.Sqlite;

using System.Data.Common;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class UtcInstantSqlTests
{
    [Theory]
    [InlineData("2026-09-08T01:02:03-04:00", "2026-09-08T05:02:03.0000000Z")]
    [InlineData("2026-09-08 05:02:03", "2026-09-08T05:02:03.0000000Z")]
    public void Stored_parameter_normalizes_foreign_timestamp_text(
        string stored,
        string expected)
    {
        using SqliteCommand command = new();

        SqliteParameter parameter = UtcInstantSql.AddStoredParameter(
            command,
            "$createdAt",
            stored);

        Assert.Equal(expected, parameter.Value);
        Assert.Same(parameter, command.Parameters["$createdAt"]);
    }

    [Fact]
    public void Stored_parameter_preserves_database_null()
    {
        using SqliteCommand command = new();

        SqliteParameter parameter = UtcInstantSql.AddStoredParameter(
            command,
            "$completedAt",
            DBNull.Value);

        Assert.Equal(DBNull.Value, parameter.Value);
    }

    [Fact]
    public void Stored_parameter_converts_a_missing_optional_instant_to_database_null()
    {
        using SqliteCommand command = new();

        SqliteParameter parameter = UtcInstantSql.AddStoredParameter(
            command,
            "$completedAt",
            null);

        Assert.Equal(DBNull.Value, parameter.Value);
    }

    [Fact]
    public void Stored_parameter_rejects_a_non_timestamp_value()
    {
        using SqliteCommand command = new();

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            UtcInstantSql.AddStoredParameter(command, "$createdAt", 42L));

        Assert.Equal("storedValue", error.ParamName);
    }

    [Fact]
    public void Typed_parameter_formats_the_instant_before_binding_it()
    {
        using SqliteCommand command = new();

        SqliteParameter parameter = UtcInstantSql.AddParameter(
            command,
            "$createdAt",
            new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.FromHours(-4)));

        Assert.Equal("2026-09-08T05:02:03.0000000Z", parameter.Value);
    }

    [Fact]
    public void Typed_parameter_supports_the_provider_neutral_direct_SQL_boundary()
    {
        using DbCommand command = new SqliteCommand();

        DbParameter parameter = UtcInstantSql.AddParameter(
            command,
            "$createdAt",
            new DateTimeOffset(2026, 9, 8, 5, 2, 3, TimeSpan.Zero));

        Assert.Equal("2026-09-08T05:02:03.0000000Z", parameter.Value);
    }
}
