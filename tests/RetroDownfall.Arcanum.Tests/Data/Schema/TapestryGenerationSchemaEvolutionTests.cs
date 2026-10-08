using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 15 adds the invariant the Tapestry's generation switch assumes: at most one
/// <c>Complete</c> generation per scope, enforced by a partial unique index rather than by the
/// publishing statement alone, and reached the same way from a fresh install and from a version-14
/// installation that already holds two.
/// </summary>
public sealed class TapestryGenerationSchemaEvolutionTests
{
    private const string WorkspaceScope = "/repo";

    [Fact]
    public async Task Version_fifteen_keeps_the_newest_complete_generation_of_a_scope_and_supersedes_the_rest()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        // A scope the switch once left with two Complete generations: the newer one is current.
        await InsertAsync(connection, "older-complete", "Workspace", WorkspaceScope, "Complete", "2026-01-01T00:00:00.0000000Z");

        await InsertAsync(connection, "newer-complete", "Workspace", WorkspaceScope, "Complete", "2026-02-01T00:00:00.0000000Z");

        await InsertAsync(connection, "already-superseded", "Workspace", WorkspaceScope, "Superseded", "2025-12-01T00:00:00.0000000Z");

        // A tie on completion time is settled by the generation id, so the outcome is deterministic.
        await InsertAsync(connection, "tie-a", "Session", "S1", "Complete", "2026-03-01T00:00:00.0000000Z");

        await InsertAsync(connection, "tie-b", "Session", "S1", "Complete", "2026-03-01T00:00:00.0000000Z");

        // Rows that are not duplicates are left alone.
        await InsertAsync(connection, "sole-complete", "SessionAttachment", "s1", "Complete", "2026-01-15T00:00:00.0000000Z");

        await InsertAsync(connection, "building", "SessionAttachment", "s1", "Building", null);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(
            [
                "already-superseded=Superseded",
                "building=Building",
                "newer-complete=Complete",
                "older-complete=Superseded",
                "sole-complete=Complete",
                "tie-a=Superseded",
                "tie-b=Complete",
            ],
            await StatusesAsync(connection));
    }

    [Fact]
    public async Task An_evolved_installation_refuses_a_second_complete_generation_for_one_scope()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        await InsertAsync(connection, "first", "Workspace", WorkspaceScope, "Complete", "2026-01-01T00:00:00.0000000Z");

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        await AssertOneCompleteGenerationPerScopeAsync(connection);
    }

    [Fact]
    public async Task A_fresh_installation_refuses_a_second_complete_generation_for_one_scope()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        await InsertAsync(connection, "first", "Workspace", WorkspaceScope, "Complete", "2026-01-01T00:00:00.0000000Z");

        await AssertOneCompleteGenerationPerScopeAsync(connection);
    }

    /// <summary>
    /// The index is on the pair (kind, id) and covers only <c>Complete</c> rows: a second Complete
    /// generation of the same scope is refused, while a Building or Superseded one, or a Complete one of
    /// another scope or kind, is not.
    /// </summary>
    private static async Task AssertOneCompleteGenerationPerScopeAsync(SqliteConnection connection)
    {
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(
            () => InsertAsync(connection, "second", "Workspace", WorkspaceScope, "Complete", "2026-02-01T00:00:00.0000000Z"));

        Assert.Equal(19, refused.SqliteErrorCode);

        await InsertAsync(connection, "building", "Workspace", WorkspaceScope, "Building", null);

        await InsertAsync(connection, "superseded", "Workspace", WorkspaceScope, "Superseded", "2025-12-01T00:00:00.0000000Z");

        await InsertAsync(connection, "other-scope", "Workspace", "/other", "Complete", "2026-02-01T00:00:00.0000000Z");

        await InsertAsync(connection, "other-kind", "Session", WorkspaceScope, "Complete", "2026-02-01T00:00:00.0000000Z");
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        string generationId,
        string scopeKind,
        string scopeId,
        string status,
        string? completedAt)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO tapestry_generations
                (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion, SettingsFingerprint,
                 SummaryRecipeVersion, EmbeddingDimension, CorpusFingerprint, StartedAt, CompletedAt)
            VALUES ($id, $kind, $scope, $status, 'algorithm', 'settings', 'recipe', 8, 'corpus',
                    '2025-11-01T00:00:00.0000000Z', $completedAt);
            """;

        _ = command.Parameters.AddWithValue("$id", generationId);

        _ = command.Parameters.AddWithValue("$kind", scopeKind);

        _ = command.Parameters.AddWithValue("$scope", scopeId);

        _ = command.Parameters.AddWithValue("$status", status);

        _ = command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> StatusesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT GenerationId || '=' || Status FROM tapestry_generations ORDER BY GenerationId;";

        List<string> values = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }
}
