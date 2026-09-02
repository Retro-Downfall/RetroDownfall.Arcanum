using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The half of the reference-spelling correction that the write-side change could not reach: the rows
/// an installation already holds.
/// </summary>
/// <remarks>
/// <c>LongRunningOperations.SessionId</c> names a row in <c>Sessions</c>, whose <c>Id</c> is uppercase
/// dashed everywhere. The store wrote this column dash-free until the write side was corrected, so
/// after that correction alone an installation holds both spellings in one column and a join written
/// without <c>lower(replace(...))</c> on both sides matches only the rows written since. That is a
/// worse position than the uniform one it replaced, because it is intermittent.
///
/// <para>The pre-state is seeded by SQL, and it has to be: no production writer can produce a dash-free
/// value in this column any more, which is exactly why the rows that hold one can only be inherited.
/// Nothing the assertions read is seeded - the seed is the wrong spelling, and what is asserted is
/// whether the join finds its Session, before and after the step.</para>
/// </remarks>
public sealed class LongRunningOperationSpellingEvolutionTests
{

    /// <summary>A Session spelled the way every writer of Sessions spells one.</summary>
    private static readonly Guid SessionIdentity = new("B0000000-0000-4000-8000-0000000000A1");

    /// <summary>The operation whose reference was written before the spelling was corrected.</summary>
    private static readonly Guid InheritedOperationIdentity = new("E0000000-0000-4000-8000-0000000000A2");

    /// <summary>An operation written after it, so the step is shown to leave a correct row alone.</summary>
    private static readonly Guid CurrentOperationIdentity = new("E0000000-0000-4000-8000-0000000000A3");

    static LongRunningOperationSpellingEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task An_inherited_dash_free_session_reference_joins_its_Session_after_the_step()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionFiveFixture.ChainSet(),
            1536,
            CancellationToken.None);

        await SeedSessionAsync(connection);

        await SeedOperationAsync(connection, InheritedOperationIdentity, SessionIdentity.ToString("N"));

        await SeedOperationAsync(connection, CurrentOperationIdentity, Canonical(SessionIdentity));

        Assert.Equal(1L, await JoinedOperationCountAsync(connection));

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        Assert.Equal(2L, await JoinedOperationCountAsync(connection));

    }

    /// <summary>The join the finding says a caller would write, with no normalization on either side.</summary>
    private static async Task<long> JoinedOperationCountAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM "LongRunningOperations" operation
            JOIN "Sessions" session ON session."Id" = operation."SessionId";
            """;

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))!;

    }

    private static async Task SeedSessionAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
            VALUES (@id, 'active', @at, @at);
            """;

        _ = command.Parameters.AddWithValue("@id", Canonical(SessionIdentity));

        _ = command.Parameters.AddWithValue("@at", "2026-01-01T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

    }

    private static async Task SeedOperationAsync(
        SqliteConnection connection,
        Guid identity,
        string sessionReference)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO "LongRunningOperations"
                ("Id", "Kind", "State", "RecoveryPolicy", "SessionId", "CreatedAt", "PublicSummary")
            VALUES (@id, 'Subagent', 0, 0, @sessionId, @at, 'seeded');
            """;

        _ = command.Parameters.AddWithValue("@id", identity.ToString("N"));

        _ = command.Parameters.AddWithValue("@sessionId", sessionReference);

        _ = command.Parameters.AddWithValue("@at", "2026-01-01T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

    }

    private static string Canonical(Guid value) => value.ToString("D").ToUpperInvariant();

}
